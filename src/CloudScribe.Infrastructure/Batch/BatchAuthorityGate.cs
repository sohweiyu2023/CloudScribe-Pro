using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Combines the installed profile, the batch's consent and the rolling ledger of every saved batch.</summary>
public sealed class BatchAuthorityGate(IBatchAuthorityStore authority, IBatchQueueStore queues, TimeProvider clock, CloudScribe.Application.Documents.IDocumentLibrary? library = null,
    CloudScribe.Application.Documents.INarrationCopyStore? narrationCopies = null, Microsoft.Extensions.Logging.ILogger? logger = null)
{
    public Task<BatchAuthorityProfile?> LoadProfileAsync(CancellationToken cancellationToken = default) => authority.LoadProfileAsync(cancellationToken);

    public Task<BatchConsent?> LoadConsentAsync(Guid batchId, CancellationToken cancellationToken = default) => authority.LoadConsentAsync(batchId, cancellationToken);

    public async Task<BatchAuthorityDecision> CheckAsync(NarrationBatch batch, string accountStableId, string projectStableId, CancellationToken cancellationToken = default)
    {
        var decision = await CheckConsentAsync(batch, accountStableId, projectStableId, cancellationToken).ConfigureAwait(false);
        return decision.Allowed ? await CheckNarrationAsync(batch, batch.Included, cancellationToken).ConfigureAwait(false) ?? decision : decision;
    }

    /// <summary>Existing jobs selected for continuation remain reachable even when a new sibling is stale.</summary>
    public async Task<BatchAuthorityDecision> CheckRunAsync(NarrationBatch batch, string accountStableId, string projectStableId, CancellationToken cancellationToken = default)
    {
        var decision = await CheckConsentAsync(batch, accountStableId, projectStableId, cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed || batch.Items.Any(x => x.JobId is not null && x.State is BatchItemState.Ready or BatchItemState.Running)) return decision;
        return await CheckNarrationAsync(batch, batch.Included, cancellationToken).ConfigureAwait(false) ?? decision;
    }

    public async Task<BatchAuthorityDecision> CheckItemAsync(NarrationBatch batch, BatchItem item, string accountStableId, string projectStableId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var decision = await CheckConsentAsync(batch, accountStableId, projectStableId, cancellationToken).ConfigureAwait(false);
        return decision.Allowed ? await CheckNarrationAsync(batch, [item], cancellationToken).ConfigureAwait(false) ?? decision : decision;
    }

    private async Task<BatchAuthorityDecision> CheckConsentAsync(NarrationBatch batch, string accountStableId, string projectStableId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var profile = await authority.LoadProfileAsync(token).ConfigureAwait(false);
        var consent = await authority.LoadConsentAsync(batch.BatchId, token).ConfigureAwait(false);
        long others = profile is null ? 0 : await OtherCommitmentsAsync(batch.BatchId, profile, token).ConfigureAwait(false);
        return BatchAuthorityEvaluator.Evaluate(profile, consent, batch, accountStableId, projectStableId, others, clock.GetUtcNow());
    }

    /// <summary>Holds and approved maxima of other batches inside the rolling window.</summary>
    public async Task<long> OtherCommitmentsAsync(Guid batchId, BatchAuthorityProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var batches = (await queues.ListAsync(cancellationToken).ConfigureAwait(false)).Where(x => x.BatchId != batchId).ToArray();
        var consents = new List<BatchConsent>();
        foreach (var other in batches)
            if (await authority.LoadConsentAsync(other.BatchId, cancellationToken).ConfigureAwait(false) is { } consent) consents.Add(consent);
        return BatchSpendLedger.Committed(batches, consents, clock.GetUtcNow(), profile.RollingWindowDays, batchId);
    }

    /// <summary>Creates and saves a one-click approval. Only allowed in <see cref="BatchAuthorityMode.InAppWithinLimits"/>
    /// and only when the approval passes every ceiling; otherwise nothing is saved.</summary>
    public async Task<BatchAuthorityDecision> ApproveInAppAsync(NarrationBatch batch, string accountStableId, string projectStableId,
        BatchAdmissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchId != batch.BatchId || !string.Equals(request.SettingsSha256, batch.SettingsSha256, StringComparison.Ordinal))
            return BatchAuthorityDecision.Deny("batch.consent-files-changed", "The batch changed after its review was prepared. Review it again.");
        int unnarratable = request.Unnarratable.Count();
        if (unnarratable > 0)
            return BatchAuthorityDecision.Deny("batch.no-engine", $"{unnarratable} file(s) cannot be narrated by any allowed engine. Remove them (each row says why), then approve.");
        if (!request.LongAudioFits)
            return BatchAuthorityDecision.Deny("batch.long-audio-cap", "The Long Audio files need more than the room left in the installed Long Audio spending cap. Raise the cap with the reviewed installer, or remove some files.");
        var items = request.ProposedConsent.Items;
        var profile = await authority.LoadProfileAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (BatchAuthorityEvaluator.CheckProfile(profile, batch, accountStableId, projectStableId, now) is { } problem) return problem;
        if (!string.Equals(request.ProfileSha256, profile!.Sha256(), StringComparison.Ordinal))
            return BatchAuthorityDecision.Deny("batch.consent-profile-changed", "The batch profile changed after the review was prepared. Review it again.");
        if (profile.Mode != BatchAuthorityMode.InAppWithinLimits)
            return BatchAuthorityDecision.Deny("batch.consent-needs-review", "This computer requires batches to be reviewed and installed. Export the batch request instead.");
        var consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.InApp, profile, accountStableId, projectStableId, items, now);
        long others = await OtherCommitmentsAsync(batch.BatchId, profile, cancellationToken).ConfigureAwait(false);
        var decision = BatchAuthorityEvaluator.Evaluate(profile, consent, batch, accountStableId, projectStableId, others, now);
        if (decision.Allowed && await CheckNarrationAsync(batch, batch.Included, cancellationToken).ConfigureAwait(false) is { } invalid) return invalid;
        if (decision.Allowed) await authority.SaveInAppConsentAsync(consent, cancellationToken).ConfigureAwait(false);
        return decision;
    }
    private async Task<BatchAuthorityDecision?> CheckNarrationAsync(NarrationBatch batch, IEnumerable<BatchItem> items, CancellationToken token)
    {
        if (!items.Any(x => x.JobId is null && x.IsIncluded && x.State != BatchItemState.Completed)) return null;
        if (library is null)
            return batch.Settings.SkipUrls ? BatchAuthorityDecision.Deny("batch.narration-validation-unavailable", "The narration copy cannot be checked in this build.") : null;
        try
        {
            foreach (var item in items.Where(x => x.JobId is null))
                await BatchNarrationIdentity.ValidateAsync(library, batch.Settings, item, narrationCopies, token).ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        {
            CloudScribe.Application.Logging.BatchDiagnostics.Source(logger, batch.BatchId, batch.BatchId,
                CloudScribe.Application.Logging.BatchSourceOutcome.NarrationInvalidated);
            return BatchAuthorityDecision.Deny("batch.narration-changed", "The original or narration copy changed. Prepare and review a new batch; nothing was sent.");
        }
    }

}
