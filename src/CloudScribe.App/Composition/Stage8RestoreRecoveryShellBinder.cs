using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

public static class Stage8RestoreRecoveryShellBinder
{
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

        if (!Path.IsPathFullyQualified(journalPath) ||
            !Path.IsPathFullyQualified(stagingRoot) ||
            !Path.IsPathFullyQualified(backupRoot))
        {
            throw new InvalidOperationException("Stage 8 restore recovery paths must be explicitly fully qualified.");
        }

        string absoluteJournalPath = Path.GetFullPath(journalPath);
        string absoluteStagingRoot = Path.GetFullPath(stagingRoot);
        string absoluteBackupRoot = Path.GetFullPath(backupRoot);

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
}
