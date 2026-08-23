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

    public static BackupRestoreArchiveInspection Inspect(string archivePath, int maximumEntries = 100_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
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
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':'))
                    traversalSafe = false;

                var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Any(static segment => segment == ".."))
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
