using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Safety;

public sealed class RestoreRecoveryProductionConfigurationResolver
{
    private readonly AppPaths _paths;
    private readonly string? _authenticationKeyTargetName;

    public RestoreRecoveryProductionConfigurationResolver(
        AppPaths paths,
        string? authenticationKeyTargetName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _authenticationKeyTargetName = authenticationKeyTargetName;
    }

    public RestoreRecoveryProductionConfiguration Resolve()
    {
        if (string.IsNullOrWhiteSpace(_authenticationKeyTargetName))
        {
            throw new InvalidOperationException(
                "Stage 8 restore recovery authentication key target is not explicitly configured.");
        }

        string recoveryRoot = Path.Combine(_paths.RootDirectory, "restore-recovery");
        string journalPath = Path.Combine(recoveryRoot, "journal.json");
        string stagingRoot = Path.Combine(recoveryRoot, "staging");
        string backupRoot = _paths.BackupsDirectory;

        if (!Path.IsPathFullyQualified(journalPath) ||
            !Path.IsPathFullyQualified(stagingRoot) ||
            !Path.IsPathFullyQualified(backupRoot))
        {
            throw new InvalidOperationException(
                "Stage 8 restore recovery production paths must resolve from the owned application-data root.");
        }

        return new RestoreRecoveryProductionConfiguration(
            new CredentialReference(_authenticationKeyTargetName),
            Path.GetFullPath(journalPath),
            Path.GetFullPath(stagingRoot),
            Path.GetFullPath(backupRoot));
    }
}
