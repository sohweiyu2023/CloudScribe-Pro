using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Generation;

internal sealed class FilePrivateProviderErrorStore(string directory, TimeProvider clock) : IPrivateProviderErrorPersistence
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
        if (bytes.Length > 4096 || Encoding.UTF8.GetByteCount(record.Message) > 2048) throw new InvalidDataException("Private record is not bounded.");
        var records = ReadRecordsCore();
        if (records.Count >= 8) File.Delete(RecordPath(records.OrderBy(x => x.CreatedUtc).First().Id));
        string temp = Path.Combine(_directory, record.Id.ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(flushToDisk: true); }
            ValidateDirectory(); File.Move(temp, RecordPath(record.Id), overwrite: false);
        }
        finally { if (File.Exists(temp)) { ValidateDirectory(); RejectLink(new FileInfo(temp)); File.Delete(temp); } }
        return true;
    }

    /// <summary>Explicit private-view read. Expired records are unavailable and removed on store access.
    /// No normal export or logger uses this store. External copies/backups are not automatically purged.</summary>
    public IReadOnlyList<PrivateProviderErrorRecord> Read()
    {
        lock (_gate)
        {
            try { return ReadRecordsCore(); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { return []; }
        }
    }

    private List<PrivateProviderErrorRecord> ReadRecordsCore()
    {
        ValidateDirectory(); List<PrivateProviderErrorRecord> records = [];
        var files = new DirectoryInfo(_directory).GetFiles();
        if (files.Length > 9) throw new InvalidDataException("Private diagnostic directory exceeds its bound.");
        foreach (var file in files)
        {
            RejectLink(file);
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out var id)
                || file.Length is < 0 or > 4096) throw new InvalidDataException("Unexpected private diagnostic file.");
            if (string.Equals(file.Extension, ".tmp", StringComparison.Ordinal)) { File.Delete(file.FullName); continue; }
            if (!string.Equals(file.Extension, ".json", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected private diagnostic file.");
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 4096) throw new InvalidDataException("Private diagnostic file changed its bound.");
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            var record = JsonSerializer.Deserialize<PrivateProviderErrorRecord>(bytes) ?? throw new InvalidDataException("Invalid private diagnostic record.");
            if (record.Id != id || record.CreatedUtc > clock.GetUtcNow() || Encoding.UTF8.GetByteCount(record.Message) > 2048
                || record.PayloadSha256.Length != 64 || !record.PayloadSha256.All(char.IsAsciiHexDigit)
                || !Guid.TryParseExact(record.TransportCorrelation, "N", out _))
                throw new InvalidDataException("Private diagnostic record identity or bound changed.");
            if (clock.GetUtcNow() - record.CreatedUtc >= Retention) { stream.Dispose(); File.Delete(file.FullName); }
            else records.Add(record);
        }
        if (records.Count > 8) throw new InvalidDataException("Private diagnostic count exceeded.");
        return records.OrderByDescending(x => x.CreatedUtc).ToList();
    }

    private string RecordPath(Guid id) { ValidateDirectory(); return Path.Combine(_directory, id.ToString("N") + ".json"); }
    private void ValidateDirectory() => PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(_directory, "Private diagnostic path must be physical.");
    private static void RejectLink(FileInfo file)
    {
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Private diagnostic links are not supported.");
    }
}
