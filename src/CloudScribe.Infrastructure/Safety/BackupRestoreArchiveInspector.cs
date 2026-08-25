using System.IO.Compression;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed record BackupRestoreArchiveInspection(
    bool ArchiveStructureValid,
    bool SecretsExcluded,
    bool NativePayloadsAllowed,
    bool PathTraversalSafe,
    int EntryCount);

public static class BackupRestoreArchiveInspector
{
    private static readonly string[] SecretNameTokens = ["secret", "credential", "apikey", "api-key", "token", "password", "vault"];
    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".msi", ".bat", ".cmd", ".ps1", ".com", ".scr"
    };

    public static BackupRestoreArchiveInspection Inspect(
        string archivePath,
        int maximumEntries = 100_000,
        long maximumSingleEntryBytes = 4L * 1024 * 1024 * 1024,
        long maximumDeclaredUncompressedBytes = 32L * 1024 * 1024 * 1024,
        int maximumCompressionRatio = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumSingleEntryBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSingleEntryBytes));
        if (maximumDeclaredUncompressedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDeclaredUncompressedBytes));
        if (maximumCompressionRatio <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCompressionRatio));
        if (!File.Exists(archivePath))
            return new(false, false, false, false, 0);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries)
                return new(false, false, false, false, archive.Entries.Count);

            var secretsExcluded = true;
            var nativeAllowed = true;
            var traversalSafe = true;
            long declaredUncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0 || entry.Length > maximumSingleEntryBytes)
                    return new(false, false, false, false, archive.Entries.Count);

                if (entry.Length > 0)
                {
                    if (entry.CompressedLength <= 0)
                        return new(false, false, false, false, archive.Entries.Count);
                    var ratio = (double)entry.Length / entry.CompressedLength;
                    if (ratio > maximumCompressionRatio)
                        return new(false, false, false, false, archive.Entries.Count);
                }

                try
                {
                    declaredUncompressedBytes = checked(declaredUncompressedBytes + entry.Length);
                }
                catch (OverflowException)
                {
                    return new(false, false, false, false, archive.Entries.Count);
                }
                if (declaredUncompressedBytes > maximumDeclaredUncompressedBytes)
                    return new(false, false, false, false, archive.Entries.Count);

                var normalized = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':'))
                    traversalSafe = false;

                var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal)))
                    traversalSafe = false;

                var fileName = Path.GetFileName(normalized);
                var lower = fileName.ToLowerInvariant();
                if (SecretNameTokens.Any(lower.Contains))
                    secretsExcluded = false;
                if (NativeExtensions.Contains(Path.GetExtension(fileName)))
                    nativeAllowed = false;
            }

            return new(true, secretsExcluded, nativeAllowed, traversalSafe, archive.Entries.Count);
        }
        catch (InvalidDataException)
        {
            return new(false, false, false, false, 0);
        }
        catch (IOException)
        {
            return new(false, false, false, false, 0);
        }
    }

    public static BackupRestoreDecision Admit(
        string archivePath,
        bool manifestAuthenticated,
        bool schemaSupported)
    {
        var inspection = Inspect(archivePath);
        return BackupRestoreAdmissionPolicy.Evaluate(
            inspection.ArchiveStructureValid,
            manifestAuthenticated,
            schemaSupported,
            inspection.SecretsExcluded,
            inspection.NativePayloadsAllowed,
            inspection.PathTraversalSafe);
    }
}
