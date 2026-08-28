using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class VoiceLabAuditionExecutionService
{
    private readonly VoiceLabAuditionCoordinator _coordinator;
    private readonly VoiceLabCatalogSelection _selection;

    public VoiceLabAuditionExecutionService(
        VoiceLabAuditionCoordinator coordinator,
        VoiceLabCatalogSelection selection)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _selection = (selection ?? throw new ArgumentNullException(nameof(selection))).Validate();
    }

    public Task<VoiceLabAuditionOutcome> ExecuteAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var currentSelection = _selection.Validate();
        return ExecuteValidatedAsync(request, currentSelection, cancellationToken);
    }

    public Task<VoiceLabAuditionOutcome> ExecuteWithCurrentSelectionAsync(
        VoiceLabAuditionRequest request,
        VoiceLabCatalogSelection currentSelection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        currentSelection = (currentSelection ?? throw new ArgumentNullException(nameof(currentSelection))).Validate();

        if (!SameVoiceTrustIdentity(_selection, currentSelection))
            throw new InvalidOperationException("Voice Lab catalog trust identity or capability evidence changed after selection; the user must reselect the voice before auditioning.");

        return ExecuteValidatedAsync(request, currentSelection, cancellationToken);
    }

    public string CapabilityEvidenceId => _selection.CapabilityEvidenceId;

    private Task<VoiceLabAuditionOutcome> ExecuteValidatedAsync(
        VoiceLabAuditionRequest request,
        VoiceLabCatalogSelection currentSelection,
        CancellationToken cancellationToken) =>
        _coordinator.ExecuteBoundAsync(
            request,
            currentSelection.ProviderStableId,
            currentSelection.AccountStableId,
            currentSelection.ProjectStableId,
            currentSelection.VoiceStableId,
            currentSelection.VoiceFingerprint,
            cancellationToken);

    private static bool SameVoiceTrustIdentity(VoiceLabCatalogSelection selected, VoiceLabCatalogSelection current) =>
        string.Equals(selected.ProviderStableId, current.ProviderStableId, StringComparison.Ordinal) &&
        string.Equals(selected.AccountStableId, current.AccountStableId, StringComparison.Ordinal) &&
        string.Equals(selected.ProjectStableId, current.ProjectStableId, StringComparison.Ordinal) &&
        string.Equals(selected.VoiceStableId, current.VoiceStableId, StringComparison.Ordinal) &&
        string.Equals(selected.VoiceFingerprint, current.VoiceFingerprint, StringComparison.Ordinal) &&
        string.Equals(selected.CapabilityEvidenceId, current.CapabilityEvidenceId, StringComparison.Ordinal);
}
