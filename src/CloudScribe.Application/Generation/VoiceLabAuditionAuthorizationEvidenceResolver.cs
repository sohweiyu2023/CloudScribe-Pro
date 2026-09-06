namespace CloudScribe.Application.Generation;

public sealed class VoiceLabAuditionAuthorizationEvidenceResolver
{
    private readonly Func<VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence?>> _loadCurrentEvidence;

    public VoiceLabAuditionAuthorizationEvidenceResolver(
        Func<VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence?>> loadCurrentEvidence)
    {
        _loadCurrentEvidence = loadCurrentEvidence ?? throw new ArgumentNullException(nameof(loadCurrentEvidence));
    }

    public async Task<VoiceLabAuditionAuthorizationEvidence> ResolveAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var evidence = await _loadCurrentEvidence(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab audition current authorization evidence is unavailable.");

        evidence.Validate();
        VoiceLabAuditionRequestBindingPolicy.RequireBoundSelection(
            request.Selection,
            evidence.Selection.ProviderStableId,
            evidence.Selection.AccountStableId,
            evidence.Selection.ProjectStableId,
            evidence.Selection.VoiceStableId,
            evidence.Selection.VoiceFingerprint);

        if (!string.Equals(
                request.Selection.CapabilityEvidenceId,
                evidence.Selection.CapabilityEvidenceId,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Voice Lab audition current capability evidence does not match the request.");
        if (!request.PricingCurrent || !evidence.PricingCurrent)
            throw new InvalidOperationException("Voice Lab audition current pricing evidence is unavailable or stale.");
        if (!request.ExplicitSpendApproved || !evidence.SpendApproved)
            throw new InvalidOperationException("Voice Lab audition current spend authorization is unavailable or revoked.");

        return evidence;
    }
}
