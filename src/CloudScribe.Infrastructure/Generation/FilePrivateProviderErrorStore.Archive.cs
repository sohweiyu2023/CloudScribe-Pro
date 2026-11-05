using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

internal sealed partial class FilePrivateProviderErrorStore
{
    public PrivateProviderErrorReadResult ReadWithStatus(Guid recordId)
    {
        lock (_gate)
        {
            try
            {
                ValidateDirectory(); PrivateProviderErrorRecord? found = null;
                foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*" + recordId.ToString("N") + ".*"))
                {
                    RejectLink(file);
                    if (!TryRecordIdentity(file, out var id, out _) || id != recordId)
                        throw new InvalidDataException("Private diagnostic identity is invalid.");
                    if (found is not null) throw new InvalidDataException("Private diagnostic identity is duplicated.");
                    var record = ReadRecord(file);
                    if (clock.GetUtcNow() - record.CreatedUtc >= Retention) File.Delete(file.FullName);
                    else found = record;
                }
                return new(PrivateProviderErrorReadStatus.Success, found is null ? [] : [found]);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { return new(PrivateProviderErrorReadStatus.Failed, []); }
        }
    }

    private bool TryRecordIdentity(FileInfo file, out Guid id, out DateTimeOffset? archivedAt)
    {
        archivedAt = null; id = Guid.Empty;
        string name = Path.GetFileNameWithoutExtension(file.Name);
        int prefix = name.StartsWith("article-", StringComparison.Ordinal) ? 8 : name.StartsWith("error-", StringComparison.Ordinal) ? 6 : 0;
        if (prefix == 0) return Guid.TryParseExact(name, "N", out id);
        if (name.Length != prefix + 52 || name[prefix + 19] != '-'
            || !string.Equals(file.Extension, ".pdiag", StringComparison.Ordinal) && (prefix == 8 || !string.Equals(file.Extension, ".json", StringComparison.Ordinal))
            || !long.TryParse(name.AsSpan(prefix, 19), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks
            || !Guid.TryParseExact(name.AsSpan(prefix + 20), "N", out id)) return false;
        archivedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return archivedAt <= clock.GetUtcNow();
    }

    private PrivateProviderErrorRecord ReadRecord(FileInfo file)
    {
        RejectLink(file);
        if (!TryRecordIdentity(file, out var id, out var archivedAt)) throw new InvalidDataException("Private record name changed.");
        bool protectedEnvelope = string.Equals(file.Extension, ".pdiag", StringComparison.Ordinal);
        if (!protectedEnvelope && !string.Equals(file.Extension, ".json", StringComparison.Ordinal)) throw new InvalidDataException("Private record format changed.");
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > (protectedEnvelope ? PrivateRecordProtection.MaximumProtectedBytes : 4096))
            throw new InvalidDataException("Private diagnostic file changed its bound.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (protectedEnvelope) bytes = PrivateRecordProtection.Unprotect(bytes);
        var record = JsonSerializer.Deserialize<PrivateProviderErrorRecord>(bytes) ?? throw new InvalidDataException("Invalid private diagnostic record.");
        if (record.Id != id || record.CreatedUtc > clock.GetUtcNow() || Encoding.UTF8.GetByteCount(record.Message) > 2048
            || record.ArticleJobId is null != (record.ArticleOrdinal is null) || record.ArticleJobId == Guid.Empty || record.ArticleOrdinal is < 1 or > 10_000
            || record.PayloadSha256.Length != 64 || !record.PayloadSha256.All(char.IsAsciiHexDigit)
            || !Guid.TryParseExact(record.TransportCorrelation, "N", out _)
            || protectedEnvelope != (record.Response is not null)
            || record.ArticleJobId is not null && record.Response is null
            || archivedAt is not null && (record.CreatedUtc != archivedAt || IsSuccessMetadata(record)
                || file.Name.StartsWith("article-", StringComparison.Ordinal) != IsArticleError(record))
            || record.Response is { } response && (response.Version != 1 || Encoding.UTF8.GetByteCount(response.RedactedBody) > 16384))
            throw new InvalidDataException("Private diagnostic record identity or bound changed.");
        return record;
    }
}
