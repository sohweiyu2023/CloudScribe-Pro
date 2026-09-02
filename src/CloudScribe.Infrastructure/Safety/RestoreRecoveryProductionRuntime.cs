namespace CloudScribe.Infrastructure.Safety;

public sealed class RestoreRecoveryProductionRuntime
{
    private readonly RestoreRecoveryProductionConfigurationResolver _configurationResolver;
    private readonly RestoreRecoveryExecutionCompositionFactory _compositionFactory;
    private readonly AtomicVerifiedRestoreExecutor _restoreExecutor;

    public RestoreRecoveryProductionRuntime(
        RestoreRecoveryProductionConfigurationResolver configurationResolver,
        RestoreRecoveryExecutionCompositionFactory compositionFactory,
        AtomicVerifiedRestoreExecutor restoreExecutor)
    {
        _configurationResolver = configurationResolver ?? throw new ArgumentNullException(nameof(configurationResolver));
        _compositionFactory = compositionFactory ?? throw new ArgumentNullException(nameof(compositionFactory));
        _restoreExecutor = restoreExecutor ?? throw new ArgumentNullException(nameof(restoreExecutor));
    }

    public async Task<string?> RecoverPersistedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreRecoveryProductionConfiguration configuration = _configurationResolver.Resolve();
        using RestoreRecoveryExecutionComposition composition = await _compositionFactory
            .CreateAsync(
                configuration.AuthenticationKeyReference,
                configuration.JournalPath,
                configuration.StagingRoot,
                configuration.BackupRoot,
                _restoreExecutor,
                cancellationToken)
            .ConfigureAwait(false);

        return await composition.Service
            .RecoverPersistedAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
