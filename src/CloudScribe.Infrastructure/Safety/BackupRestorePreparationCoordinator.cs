using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public static class BackupRestorePreparationCoordinator
{
    public static BackupRestorePreparationResult Prepare(
        string archivePath,
        string stagingRoot,
        ReadOnlySpan<byte> canonicalManifestBytes,
        ReadOnlySpan<byte> signatureDer,
        string trustedPublicKeyPem,
        bool schemaSupported,
        long maximumExtractedBytes = 4L * 1024L * 1024L * 1024L)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        BackupRestoreManifestVerifier.RequireAuthenticated(
            canonicalManifestBytes,
            signatureDer,
            trustedPublicKeyPem);

        var decision = BackupRestoreArchiveInspector.Admit(
            archivePath,
            manifestAuthenticated: true,
            schemaSupported: schemaSupported);

        if (!decision.MayRestore || !string.Equals(decision.Reason, "restore-admitted", StringComparison.Ordinal))
            throw new InvalidDataException($"Backup archive was not admitted for restore: {decision.Reason}");

        var staging = BackupRestoreStagingExtractor.ExtractAdmittedArchive(
            archivePath,
            stagingRoot,
            decision,
            maximumExtractedBytes);

        return new(decision, staging);
    }
}
