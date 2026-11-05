using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Generation;

internal sealed partial class FilePrivateProviderErrorStore(string directory, TimeProvider clock) : IPrivateProviderErrorPersistence
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly System.Threading.Lock _gate = new();
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    public bool Save(PrivateProviderErrorRecord record)
    {
        lock (_gate) return SaveCore(record);
    }

    private bool SaveCore(PrivateProviderErrorRecord record)
    {
        if (clock.GetUtcNow() - record.CreatedUtc >= Retention) return false;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        bool protectedEnvelope = record.Response is not null;
        if (bytes.Length > (protectedEnvelope ? PrivateRecordProtection.MaximumPlaintextBytes : 4096)
            || record.ArticleJobId is null != (record.ArticleOrdinal is null) || record.ArticleJobId == Guid.Empty || record.ArticleOrdinal is < 1 or > 10_000
            || record.ArticleJobId is not null && record.Response is null
            || Encoding.UTF8.GetByteCount(record.Message) > 2048
            || record.Response is { } response && (response.Version != 1 || Encoding.UTF8.GetByteCount(response.RedactedBody) > 16384))
            throw new InvalidDataException("Private record is not bounded.");
        if (protectedEnvelope) bytes = PrivateRecordProtection.Protect(bytes);
        var records = ReadLegacyRecords();
        string temp = Path.Combine(_directory, record.Id.ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(flushToDisk: true); }
            ValidateDirectory(); File.Move(temp, RecordPath(record), overwrite: false);
            // Preserve older evidence until the replacement is durably written and published.
            var samePool = records.Where(IsSuccessMetadata).ToArray();
            if (IsSuccessMetadata(record) && samePool.Length >= 8)
            {
                var oldest = samePool.OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).First();
                File.Delete(ExistingRecordPath(oldest));
            }
        }
        finally { if (File.Exists(temp)) { ValidateDirectory(); RejectLink(new FileInfo(temp)); File.Delete(temp); } }
        return true;
    }

    /// <summary>Explicit private-view read. Expired records are unavailable and removed on store access.
    /// No normal export or logger uses this store. External copies/backups are not automatically purged.</summary>
    public IReadOnlyList<PrivateProviderErrorRecord> Read() => ReadWithStatus().Records;

    public PrivateProviderErrorReadResult ReadWithStatus()
    {
        lock (_gate)
        {
            try { return new(PrivateProviderErrorReadStatus.Success, ReadRecordsCore()); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                var status = error switch
                {
                    InvalidDataException or JsonException or System.Security.Cryptography.CryptographicException => PrivateProviderErrorReadStatus.CorruptOrUnprotectable,
                    UnauthorizedAccessException => PrivateProviderErrorReadStatus.AccessDenied,
                    IOException => PrivateProviderErrorReadStatus.Unavailable,
                    _ => PrivateProviderErrorReadStatus.Failed
                };
                return new(status, []);
            }
        }
    }

    private List<PrivateProviderErrorRecord> ReadLegacyRecords() => ReadRecordsCore(legacyOnly: true);

    private List<PrivateProviderErrorRecord> ReadRecordsCore(bool legacyOnly = false)
    {
        ValidateDirectory(); List<PrivateProviderErrorRecord> records = [];
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
        {
            RejectLink(file);
            if (!TryRecordIdentity(file, out _, out var archivedAt)
                || file.Length is < 0 or > PrivateRecordProtection.MaximumProtectedBytes
                || archivedAt is not null && file.Length == 0) throw new InvalidDataException("Unexpected private diagnostic file.");
            if (string.Equals(file.Extension, ".tmp", StringComparison.Ordinal)) { File.Delete(file.FullName); continue; }
            if (archivedAt is { } created && clock.GetUtcNow() - created >= Retention) { File.Delete(file.FullName); continue; }
            if (legacyOnly && archivedAt is not null) continue; // Enumerate metadata, never decrypt article history on each response.
            var record = ReadRecord(file);
            if (clock.GetUtcNow() - record.CreatedUtc >= Retention) File.Delete(file.FullName);
            else records.Add(record);
        }
        // Error history is age-retained. Only body-free successes have a rotating display-metadata pool.
        foreach (bool success in new[] { true })
        {
            var pool = records.Where(x => IsSuccessMetadata(x) == success).ToArray();
            pool = pool.Where(x => !IsArticleError(x)).ToArray();
            if (pool.Length > 9) throw new InvalidDataException("Private diagnostic pool exceeds its bound.");
            if (pool.Length == 9)
            {
                var oldest = pool.OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).First();
                File.Delete(ExistingRecordPath(oldest)); records.Remove(oldest);
            }
        }
        return records.OrderByDescending(x => x.CreatedUtc).ToList();
    }

    private static bool IsSuccessMetadata(PrivateProviderErrorRecord record) => record.Response is
    {
        HttpStatus: >= 200 and < 300, ParseState: "SuccessBodyExcluded", RedactedBody.Length: 0, DetailsOmitted: true,
        ErrorStatus: CloudScribe.Domain.Generation.GoogleErrorStatus.NotProvided
    };

    private static bool IsArticleError(PrivateProviderErrorRecord record) => record.ArticleJobId is not null && !IsSuccessMetadata(record);

    private string RecordPath(PrivateProviderErrorRecord record) => !IsSuccessMetadata(record)
        ? Path.Combine(_directory, (IsArticleError(record) ? "article-" : "error-")
            + record.CreatedUtc.UtcTicks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture) + "-" + record.Id.ToString("N")
            + (record.Response is not null ? ".pdiag" : ".json"))
        : RecordPath(record.Id, record.Response is not null);

    private string ExistingRecordPath(PrivateProviderErrorRecord record)
    {
        string modern = RecordPath(record);
        return File.Exists(modern) ? modern : RecordPath(record.Id, record.Response is not null);
    }

    private string RecordPath(Guid id, bool protectedEnvelope = false) { ValidateDirectory(); return Path.Combine(_directory, id.ToString("N") + (protectedEnvelope ? ".pdiag" : ".json")); }
    private void ValidateDirectory() => PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(_directory, "Private diagnostic path must be physical.");
    private static void RejectLink(FileInfo file)
    {
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Private diagnostic links are not supported.");
    }
}
