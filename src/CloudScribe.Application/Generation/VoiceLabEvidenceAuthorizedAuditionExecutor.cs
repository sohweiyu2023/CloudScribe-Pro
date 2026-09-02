using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed class VoiceLabEvidenceAuthorizedAuditionExecutor : IVoiceLabAuthorizedAuditionExecutor
{
    private readonly VoiceLabAuditionAuthorizationEvidence _approvedEvidence;
    private readonly Func<VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence>> _currentEvidenceResolver;
    private readonly Func<string, string, CancellationToken, ValueTask<IVoiceLabAuditionProviderAdapter>> _resolveProviderAdapter;

    public VoiceLabEvidenceAuthorizedAuditionExecutor(
        VoiceLabAuditionAuthorizationEvidence approvedEvidence,
        Func<VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence>> currentEvidenceResolver,
        Func<string, string, CancellationToken, ValueTask<IVoiceLabAuditionProviderAdapter>> resolveProviderAdapter)
    {
        _approvedEvidence = (approvedEvidence ?? throw new ArgumentNullException(nameof(approvedEvidence))).Validate();
        _currentEvidenceResolver = currentEvidenceResolver ?? throw new ArgumentNullException(nameof(currentEvidenceResolver));
        _resolveProviderAdapter = resolveProviderAdapter ?? throw new ArgumentNullException(nameof(resolveProviderAdapter));
    }

    public async Task<GenerationProviderResponse> SubmitAuthorizedAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedEvidence = _approvedEvidence.Validate();
        RequireRequestStillBound(request, selectedEvidence);

        var currentEvidence = await ResolveCurrentEvidenceAsync(request, cancellationToken).ConfigureAwait(false);
        selectedEvidence.EnsureStillAuthorized(currentEvidence);

        var adapter = await _resolveProviderAdapter(
            currentEvidence.Selection.ProviderStableId,
            currentEvidence.Selection.AccountStableId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab audition provider adapter is unavailable.");

        await using (adapter.ConfigureAwait(false))
        {
            if (!string.Equals(
                    adapter.Descriptor.StableId,
                    currentEvidence.Selection.ProviderStableId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Voice Lab audition provider adapter identity changed before submission.");

            var submissionEvidence = await ResolveCurrentEvidenceAsync(request, cancellationToken).ConfigureAwait(false);
            selectedEvidence.EnsureStillAuthorized(submissionEvidence);
            currentEvidence.EnsureStillAuthorized(submissionEvidence);

            var providerRequest = new VoiceLabProviderAuditionRequest(
                submissionEvidence.Selection.ProviderStableId,
                submissionEvidence.Selection.AccountStableId,
                submissionEvidence.Selection.ProjectStableId,
                submissionEvidence.Selection.VoiceStableId,
                submissionEvidence.Selection.VoiceFingerprint,
                submissionEvidence.Selection.CapabilityEvidenceId,
                submissionEvidence.CredentialReferenceId,
                submissionEvidence.PricingEvidenceId,
                submissionEvidence.SpendAuthorizationId,
                submissionEvidence.AccountRevision,
                request.OutputFormat,
                request.ForceFresh).Validate();

            return await adapter.SubmitVoiceLabAuditionAsync(providerRequest, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Voice Lab audition provider returned no response.");
        }
    }

    private async Task<VoiceLabAuditionAuthorizationEvidence> ResolveCurrentEvidenceAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken)
    {
        var evidence = await _currentEvidenceResolver(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab audition current authorization evidence is unavailable.");
        RequireRequestStillBound(request, evidence);
        return evidence.Validate();
    }

    private static void RequireRequestStillBound(
        VoiceLabAuditionRequest request,
        VoiceLabAuditionAuthorizationEvidence evidence)
    {
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
            throw new InvalidOperationException("Voice Lab audition capability evidence changed after approval.");
        if (!request.ExplicitSpendApproved || !evidence.SpendApproved)
            throw new InvalidOperationException("Voice Lab audition requires explicit current spend approval.");
        if (!request.PricingCurrent || !evidence.PricingCurrent)
            throw new InvalidOperationException("Voice Lab audition requires current pricing evidence.");
    }
}
