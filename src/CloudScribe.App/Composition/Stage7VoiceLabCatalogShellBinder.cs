using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed class Stage7VoiceLabCatalogShellBinder(
    VoiceLabCatalogQueryService catalogService,
    IVoiceLabProjectAuthorizationStore projectAuthorizations,
    VoiceLabCatalogCurrentEvidenceResolver currentEvidence,
    TimeProvider timeProvider)
{
    public void Bind(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        viewModel.ConfigureStage7VoiceLabCatalog(catalogService, CaptureCurrentStateAsync);
    }

    public async Task<VoiceLabCatalogUiState> CaptureCurrentStateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset nowUtc = timeProvider.GetUtcNow();
        IReadOnlyList<VoiceLabProjectAuthorizationEvidence> persisted = await projectAuthorizations
            .ListCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        VoiceLabProjectAuthorizationEvidence[] current = persisted
            .Where(item => item.ProjectAuthorized && item.IsCurrent(nowUtc))
            .ToArray();
        if (current.Length != 1)
        {
            throw new InvalidOperationException(
                current.Length == 0
                    ? "Voice Lab requires exactly one current persisted project authorization before catalog access."
                    : "Voice Lab catalog project selection is ambiguous; exactly one current persisted project authorization is required.");
        }

        VoiceLabProjectAuthorizationEvidence selected = current[0];
        var query = new VoiceLabCatalogQuery(
            selected.ProviderId,
            selected.AccountId,
            selected.ProjectId,
            SearchText: null,
            Locale: null,
            IncludePrivateVoices: false);

        VoiceLabCatalogAuthorizationEvidence evidence = await currentEvidence
            .ResolveAsync(query, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog authorization is no longer current.");

        return new VoiceLabCatalogUiState(
            query,
            AccountAuthorized: true,
            ProjectAuthorized: evidence.ProjectAuthorized,
            PrivateVoiceAccessAuthorized: evidence.PrivateVoiceAccessAuthorized);
    }
}
