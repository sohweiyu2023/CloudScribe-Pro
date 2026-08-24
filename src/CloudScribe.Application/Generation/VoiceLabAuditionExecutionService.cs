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
        return _coordinator.ExecuteBoundAsync(
            request,
            currentSelection.ProviderStableId,
            currentSelection.AccountStableId,
            currentSelection.ProjectStableId,
            currentSelection.VoiceStableId,
            currentSelection.VoiceFingerprint,
            cancellationToken);
    }

    public string CapabilityEvidenceId => _selection.CapabilityEvidenceId;
}
