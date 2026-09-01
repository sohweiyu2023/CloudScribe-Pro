using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabProductionAuditionEvidenceLoader
{
    private readonly IProviderAccountStore _accounts;
    private readonly IProviderCapabilitySnapshotStore _capabilities;
    private readonly Func<VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence?>> _loadCurrentEvidence;
    private readonly TimeProvider _timeProvider;

    public VoiceLabProductionAuditionEvidenceLoader(
        IProviderAccountStore accounts,
        IProviderCapabilitySnapshotStore capabilities,
        Func<VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence?>> loadCurrentEvidence,
        TimeProvider timeProvider)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _loadCurrentEvidence = loadCurrentEvidence ?? throw new ArgumentNullException(nameof(loadCurrentEvidence));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<VoiceLabAuditionAuthorizationEvidence?> LoadAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.Selection.Validate();

        if (!request.PricingCurrent)
            throw new InvalidOperationException("Voice Lab audition request pricing is no longer current.");
        if (!request.ExplicitSpendApproved)
            throw new InvalidOperationException("Voice Lab audition request no longer has explicit spend approval.");

        VoiceLabAuditionAuthorizationEvidence? evidence = await _loadCurrentEvidence(
            request,
            cancellationToken).ConfigureAwait(false);
        if (evidence is null)
            return null;

        evidence.Validate();

        if (!Equals(evidence.Selection, request.Selection))
            throw new InvalidOperationException("Voice Lab audition authorization selection changed after the request was bound.");

        ProviderAccountSnapshot account = await _accounts.FindAsync(
            evidence.Selection.ProviderStableId,
            evidence.Selection.AccountStableId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab audition provider account is no longer available.");

        if (!account.IsEnabled)
            throw new InvalidOperationException("Voice Lab audition provider account is disabled.");

        // The persisted account snapshot is authoritative for revision binding. Do not
        // trust a compatibility/default revision supplied by UI or caller evidence.
        evidence = evidence with { AccountRevision = account.Revision };
        evidence.Validate();

        string credentialReferenceId = account.Reference.CredentialReference?.TargetName
            ?? throw new InvalidOperationException("Voice Lab audition provider account has no credential reference.");
        if (!string.Equals(credentialReferenceId, evidence.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Voice Lab audition credential reference changed after authorization.");

        StoredProviderCapabilitySnapshot capability = await _capabilities.GetLatestAsync(
            evidence.Selection.ProviderStableId,
            evidence.Selection.AccountStableId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab audition capability evidence is unavailable.");

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        if (capability.IsStale(nowUtc))
            throw new InvalidOperationException("Voice Lab audition capability evidence is stale.");

        string capabilityEvidenceId = capability.Id.ToString("D");
        if (!string.Equals(capabilityEvidenceId, evidence.Selection.CapabilityEvidenceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Voice Lab audition capability evidence changed after authorization.");

        cancellationToken.ThrowIfCancellationRequested();
        return evidence;
    }
}
