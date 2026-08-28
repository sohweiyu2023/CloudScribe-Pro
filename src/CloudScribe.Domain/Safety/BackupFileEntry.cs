namespace CloudScribe.Domain.Safety;

public sealed record BackupFileEntry(string RelativePath, long Length, string Sha256)
{
    public BackupFileEntry Validate()
    {
        var (relativePath, sha256) = ValidateCore(RelativePath, Length, Sha256);
        return this with { RelativePath = relativePath, Sha256 = sha256 };
    }

    private static (string RelativePath, string Sha256) ValidateCore(string relativePath, long length, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (Path.IsPathFullyQualified(relativePath)) throw new InvalidOperationException("Backup entry paths must be relative.");
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
            throw new InvalidOperationException("Backup entry contains an unsafe path segment.");
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (sha256.Length != 64 || sha256.Any(static c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Backup entry SHA-256 must be exactly 64 hexadecimal characters.");
        return (normalized, sha256.ToLowerInvariant());
    }
}
