using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

public static class Stage8RestoreRecoveryShellBinder
{
    public static void ConfigurePersistedRecovery(
        ShellViewModel viewModel,
        RestoreRecoveryExecutionCompositionFactory compositionFactory,
        RestoreRecoveryProductionConfigurationResolver configurationResolver,
        AtomicVerifiedRestoreExecutor restoreExecutor)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(compositionFactory);
        ArgumentNullException.ThrowIfNull(configurationResolver);
        ArgumentNullException.ThrowIfNull(restoreExecutor);

        viewModel.ConfigureStage8RestoreRecovery(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreRecoveryProductionConfiguration configuration = configurationResolver.Resolve();

            using RestoreRecoveryExecutionComposition composition = await compositionFactory
                .CreateAsync(
                    configuration.AuthenticationKeyReference,
                    configuration.JournalPath,
                    configuration.StagingRoot,
                    configuration.BackupRoot,
                    restoreExecutor,
                    cancellationToken)
                .ConfigureAwait(true);

            return await composition.Service
                .RecoverPersistedAsync(cancellationToken)
                .ConfigureAwait(true);
        });
    }

    public static void ConfigurePersistedRecovery(
        ShellViewModel viewModel,
        RestoreRecoveryExecutionCompositionFactory compositionFactory,
        CredentialReference authenticationKeyReference,
        string journalPath,
        string stagingRoot,
        string backupRoot,
        AtomicVerifiedRestoreExecutor restoreExecutor)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(compositionFactory);
        ArgumentNullException.ThrowIfNull(authenticationKeyReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentNullException.ThrowIfNull(restoreExecutor);

        (string absoluteJournalPath, string absoluteStagingRoot, string absoluteBackupRoot) =
            ResolveExplicitRecoveryPaths(journalPath, stagingRoot, backupRoot);

        viewModel.ConfigureStage8RestoreRecovery(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using RestoreRecoveryExecutionComposition composition = await compositionFactory
                .CreateAsync(
                    authenticationKeyReference,
                    absoluteJournalPath,
                    absoluteStagingRoot,
                    absoluteBackupRoot,
                    restoreExecutor,
                    cancellationToken)
                .ConfigureAwait(true);

            return await composition.Service
                .RecoverPersistedAsync(cancellationToken)
                .ConfigureAwait(true);
        });
    }

    public static void Configure(
        ShellViewModel viewModel,
        RestoreRecoveryExecutionCompositionFactory compositionFactory,
        CredentialReference authenticationKeyReference,
        string journalPath,
        string stagingRoot,
        string backupRoot,
        AtomicVerifiedRestoreExecutor restoreExecutor,
        Func<CancellationToken, Task<RestoreExecutionPlan>> captureCurrentPlanAsync)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(compositionFactory);
        ArgumentNullException.ThrowIfNull(authenticationKeyReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentNullException.ThrowIfNull(restoreExecutor);
        ArgumentNullException.ThrowIfNull(captureCurrentPlanAsync);

        (string absoluteJournalPath, string absoluteStagingRoot, string absoluteBackupRoot) =
            ResolveExplicitRecoveryPaths(journalPath, stagingRoot, backupRoot);

        viewModel.ConfigureStage8RestoreRecovery(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreExecutionPlan plan = await captureCurrentPlanAsync(cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The current restore execution plan is unavailable.");

            using RestoreRecoveryExecutionComposition composition = await compositionFactory
                .CreateAsync(
                    authenticationKeyReference,
                    absoluteJournalPath,
                    absoluteStagingRoot,
                    absoluteBackupRoot,
                    restoreExecutor,
                    cancellationToken)
                .ConfigureAwait(true);

            return await composition.Service
                .RecoverAsync(plan, cancellationToken)
                .ConfigureAwait(true);
        });
    }

    private static (string JournalPath, string StagingRoot, string BackupRoot) ResolveExplicitRecoveryPaths(
        string journalPath,
        string stagingRoot,
        string backupRoot)
    {
        if (!Path.IsPathFullyQualified(journalPath) ||
            !Path.IsPathFullyQualified(stagingRoot) ||
            !Path.IsPathFullyQualified(backupRoot))
        {
            throw new InvalidOperationException("Stage 8 restore recovery paths must be explicitly fully qualified.");
        }

        return (
            Path.GetFullPath(journalPath),
            Path.GetFullPath(stagingRoot),
            Path.GetFullPath(backupRoot));
    }
}
