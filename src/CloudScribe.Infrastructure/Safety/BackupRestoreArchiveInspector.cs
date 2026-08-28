using System.IO.Compression;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSingleEntryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDeclaredUncompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCompressionRatio);
        if (!File.Exists(archivePath))
            return Invalid();

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return InspectArchive(
                archive,
                maximumEntries,
                maximumSingleEntryBytes,
                maximumDeclaredUncompressedBytes,
                maximumCompressionRatio);
        }
        catch (InvalidDataException)
        {
            return Invalid();
        }
        catch (IOException)
        {
            return Invalid();
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

    private static BackupRestoreArchiveInspection InspectArchive(
        ZipArchive archive,
        int maximumEntries,
        long maximumSingleEntryBytes,
        long maximumDeclaredUncompressedBytes,
        int maximumCompressionRatio)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries)
            return Invalid(archive.Entries.Count);

        var secretsExcluded = true;
        var nativeAllowed = true;
        var traversalSafe = true;
        long declaredUncompressedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (!TryValidateEntryBounds(
                    entry,
                    maximumSingleEntryBytes,
                    maximumDeclaredUncompressedBytes,
                    maximumCompressionRatio,
                    ref declaredUncompressedBytes))
                return Invalid(archive.Entries.Count);

            InspectEntryName(entry.FullName, ref secretsExcluded, ref nativeAllowed, ref traversalSafe);
        }

        return new(true, secretsExcluded, nativeAllowed, traversalSafe, archive.Entries.Count);
    }

    private static bool TryValidateEntryBounds(
        ZipArchiveEntry entry,
        long maximumSingleEntryBytes,
        long maximumDeclaredUncompressedBytes,
        int maximumCompressionRatio,
        ref long declaredUncompressedBytes)
    {
        if (entry.Length < 0 || entry.Length > maximumSingleEntryBytes)
            return false;
        if (entry.Length > 0 &&
            (entry.CompressedLength <= 0 || (double)entry.Length / entry.CompressedLength > maximumCompressionRatio))
            return false;

        try
        {
            declaredUncompressedBytes = checked(declaredUncompressedBytes + entry.Length);
        }
        catch (OverflowException)
        {
            return false;
        }

        return declaredUncompressedBytes <= maximumDeclaredUncompressedBytes;
    }

    private static void InspectEntryName(
        string entryName,
        ref bool secretsExcluded,
        ref bool nativeAllowed,
        ref bool traversalSafe)
    {
        var normalized = entryName.Replace('\\', '/');
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

    private static BackupRestoreArchiveInspection Invalid(int entryCount = 0) =>
        new(false, false, false, false, entryCount);
}
