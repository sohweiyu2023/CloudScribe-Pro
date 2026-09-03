using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed class Stage7VoiceLabAuditionShellBinder(
    VoiceLabCatalogQueryService catalogService,
    Stage7VoiceLabCatalogShellBinder catalogShell,
    VoiceLabAuditionCurrentEvidenceResolver currentEvidence,
    VoiceLabProductionAuthorizedAuditionExecutorFactory executorFactory)
{
    public void Bind(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        viewModel.ConfigureStage7VoiceLabAudition(
            CreateExecutionServiceAsync,
            CaptureCurrentRequestAsync,
            RefreshCurrentSelectionAsync);
    }

    private async Task<VoiceLabAuditionRequest> CaptureCurrentRequestAsync(
        VoiceLabCatalogSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        selection.Validate();
        VoiceLabAuditionAuthorizationEvidence evidence = await currentEvidence
            .ResolveAsync(selection, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab audition pricing/spend authorization is unavailable or no longer current.");
        evidence.Validate();
        if (!Equals(evidence.Selection, selection))
            throw new InvalidOperationException("Voice Lab audition authorization no longer matches the selected catalog voice.");

        return new VoiceLabAuditionRequest(
            selection,
            CachePolicyEligible: false,
            ForceFresh: true,
            ExplicitSpendApproved: evidence.SpendApproved,
            PricingCurrent: evidence.PricingCurrent,
            OutputFormat: "wav");
    }

    private async Task<VoiceLabCatalogSelection> RefreshCurrentSelectionAsync(
        VoiceLabCatalogSelection selected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        selected.Validate();
        VoiceLabCatalogUiState state = await catalogShell
            .CaptureCurrentStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(state.Query.ProviderId, selected.ProviderStableId, StringComparison.Ordinal) ||
            !string.Equals(state.Query.AccountId, selected.AccountStableId, StringComparison.Ordinal) ||
            !string.Equals(state.Query.ProjectId, selected.ProjectStableId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Voice Lab current catalog authorization moved outside the selected provider/account/project boundary.");
        }

        IReadOnlyList<VoiceLabCatalogSelection> current = await catalogService.QueryAsync(
            state.Query,
            state.AccountAuthorized,
            state.ProjectAuthorized,
            state.PrivateVoiceAccessAuthorized,
            cancellationToken).ConfigureAwait(false);
        VoiceLabCatalogSelection? refreshed = current.SingleOrDefault(candidate =>
            string.Equals(candidate.VoiceStableId, selected.VoiceStableId, StringComparison.Ordinal));
        return refreshed?.Validate()
            ?? throw new InvalidOperationException("The selected Voice Lab voice is no longer present in the current authorized catalog.");
    }

    private async Task<VoiceLabAuditionExecutionService> CreateExecutionServiceAsync(
        VoiceLabCatalogSelection selected,
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(request);
        selected.Validate();
        if (!Equals(request.Selection, selected))
            throw new InvalidOperationException("Voice Lab audition request differs from the selected catalog voice.");
        if (!request.ForceFresh || request.CachePolicyEligible)
            throw new InvalidOperationException("Production Voice Lab shell auditions are fresh-only and may not enter an unverified cache path.");

        IVoiceLabAuthorizedAuditionExecutor executor = await executorFactory
            .CreateAsync(request, RefreshCurrentSelectionAsync, cancellationToken)
            .ConfigureAwait(false);
        var coordinator = new VoiceLabAuditionCoordinator(FailClosedCacheReadAsync, executor);
        return new VoiceLabAuditionExecutionService(coordinator, selected);
    }

    private static Task<ReadOnlyMemory<byte>?> FailClosedCacheReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Fresh-only production Voice Lab auditions must never read an unverified cache entry.");
    }
}
