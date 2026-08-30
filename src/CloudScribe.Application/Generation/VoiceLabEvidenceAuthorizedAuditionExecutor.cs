using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed class VoiceLabEvidenceAuthorizedAuditionExecutor : IVoiceLabAuthorizedAuditionExecutor
{
    private readonly VoiceLabAuditionAuthorizationEvidence _approvedEvidence;
    private readonly Func<CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence>> _currentEvidenceResolver;
    private readonly Func<VoiceLabAuditionRequest, CancellationToken, Task<GenerationProviderResponse>> _submitProvider;

    public VoiceLabEvidenceAuthorizedAuditionExecutor(
        VoiceLabAuditionAuthorizationEvidence approvedEvidence,
        Func<CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence>> currentEvidenceResolver,
        Func<VoiceLabAuditionRequest, CancellationToken, Task<GenerationProviderResponse>> submitProvider)
    {
        _approvedEvidence = (approvedEvidence ?? throw new ArgumentNullException(nameof(approvedEvidence))).Validate();
        _currentEvidenceResolver = currentEvidenceResolver ?? throw new ArgumentNullException(nameof(currentEvidenceResolver));
        _submitProvider = submitProvider ?? throw new ArgumentNullException(nameof(submitProvider));
    }

    public async Task<GenerationProviderResponse> SubmitAuthorizedAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedEvidence = _approvedEvidence.Validate();
        VoiceLabAuditionRequestBindingPolicy.RequireBoundSelection(
            request.Selection,
            selectedEvidence.Selection.ProviderStableId,
            selectedEvidence.Selection.AccountStableId,
            selectedEvidence.Selection.ProjectStableId,
            selectedEvidence.Selection.VoiceStableId,
            selectedEvidence.Selection.VoiceFingerprint);

        if (!string.Equals(
                request.Selection.CapabilityEvidenceId,
                selectedEvidence.Selection.CapabilityEvidenceId,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Voice Lab audition capability evidence changed after approval.");
        if (!request.ExplicitSpendApproved || !selectedEvidence.SpendApproved)
            throw new InvalidOperationException("Voice Lab audition requires explicit current spend approval.");
        if (!request.PricingCurrent || !selectedEvidence.PricingCurrent)
            throw new InvalidOperationException("Voice Lab audition requires current pricing evidence.");

        var currentEvidence = await _currentEvidenceResolver(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab audition current authorization evidence is unavailable.");
        selectedEvidence.EnsureStillAuthorized(currentEvidence);

        return await _submitProvider(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab audition provider returned no response.");
    }
}
