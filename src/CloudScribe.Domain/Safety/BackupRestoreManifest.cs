using System.Security.Cryptography;

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

public sealed record BackupRestoreManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupFileEntry> Files)
{
    public BackupRestoreManifest Validate()
    {
        if (SchemaVersion != 1) throw new InvalidOperationException($"Unsupported backup manifest schema version: {SchemaVersion}");
        ArgumentNullException.ThrowIfNull(Files);
        if (Files.Count == 0) throw new InvalidOperationException("Backup manifest must contain at least one file.");
        var validated = Files.Select(static file => file.Validate()).ToArray();
        var duplicate = validated.GroupBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Backup manifest contains colliding output path: {duplicate.Key}");
        return this with { Files = validated };
    }

    public static async Task VerifyFileAsync(string restoreRoot, BackupFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreRoot);
        entry = entry.Validate();
        var root = Path.GetFullPath(restoreRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup restore path escapes the restore root.");
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Backup restore file is missing.", fullPath);
        if (info.Length != entry.Length) throw new InvalidDataException($"Backup restore length mismatch for {entry.RelativePath}.");
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(entry.Sha256)))
            throw new InvalidDataException($"Backup restore digest mismatch for {entry.RelativePath}.");
    }
}
