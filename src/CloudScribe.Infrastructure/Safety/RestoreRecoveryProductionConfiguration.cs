using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Safety;

public sealed record RestoreRecoveryProductionConfiguration(
    CredentialReference AuthenticationKeyReference,
    string JournalPath,
    string StagingRoot,
    string BackupRoot);
