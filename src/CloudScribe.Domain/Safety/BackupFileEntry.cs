namespace CloudScribe.Domain.Safety;

public sealed record BackupFileEntry(string RelativePath, long Length, string Sha256)
{
    public BackupFileEntry Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Sha256);
        if (Path.IsPathFullyQualified(RelativePath)) throw new InvalidOperationException("Backup entry paths must be relative.");
        var normalized = RelativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
            throw new InvalidOperationException("Backup entry contains an unsafe path segment.");
        if (Length < 0) throw new ArgumentOutOfRangeException(nameof(Length));
        if (Sha256.Length != 64 || Sha256.Any(static c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Backup entry SHA-256 must be exactly 64 hexadecimal characters.");
        return this with { RelativePath = normalized, Sha256 = Sha256.ToLowerInvariant() };
    }
}
