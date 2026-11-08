using System.Text.Json;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Chooses each file's engine and prepares its exact approval, offline (no provider call, nothing committed):
/// <list type="bullet">
/// <item>Long Audio when the batch prefers it, the profile allows it and the installed scope can take the file (same voice
/// and language, at most 1 MB): the exact request binding and the scope's own price for its ceiling.</item>
/// <item>Otherwise the ordinary engine, when allowed: its compiled plan, with the service's exact maximum when the file is
/// already admitted, or the pre-flight estimate and a "needs article admission" mark when it is not.</item>
/// <item>A file that already started keeps its earlier approval exactly, so its saved job is never orphaned.</item>
/// </list></summary>
public sealed class BatchAdmissionRequestBuilder(IDocumentLibrary library, TimeProvider clock, BatchLongAudioPlanner? longAudio = null,
    IBatchArticleWorkflow? articles = null)
{
    public async Task<BatchAdmissionRequest> BuildAsync(NarrationBatch batch, BatchAuthorityProfile profile, string accountStableId, string projectStableId,
        BatchConsent? previous = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(profile);
        var engines = BatchEngines.Candidates(batch.Settings.Engine).Where(profile.AllowsEngine).ToArray();
        BatchLongAudioPlanner.Context? la = null;
        string? laUnavailable = null;
        if (engines.Contains(BatchEngines.LongAudio, StringComparer.Ordinal))
        {
            if (!string.Equals(profile.Currency, "USD", StringComparison.Ordinal) || profile.Scale != 2) laUnavailable = "Long Audio needs a batch profile in US cents.";
            else if (longAudio is null || (la = await longAudio.ReadAsync(batch, cancellationToken).ConfigureAwait(false)) is null)
                laUnavailable = "Long Audio is not set up on this computer.";
        }
        var items = new List<BatchAdmissionRequestItem>();
        long retainedLongAudio = 0;
        foreach (var item in batch.Included.Where(x => x.State != BatchItemState.Completed).OrderBy(x => x.Order))
        {
            if (item.DocumentId is not { } documentId) throw new InvalidOperationException("Add every file to the library before approval.");
            if (item.JobId is not null && previous?.Items.SingleOrDefault(x => x.ItemId == item.ItemId) is { } kept)
            {
                if (kept.LongAudio is { } retained && la is not null)
                    retainedLongAudio = checked(retainedLongAudio + la.RetainedCredit(retained, item.JobId));
                items.Add(new(item.ItemId, item.Order, item.SourceSha256, item.Characters, documentId, kept.RevisionId, kept.Engine, kept.PlanSha256, 0,
                    kept.MaximumMinorUnits, true, false, "Already started: keeps its original approval.", null, kept.LongAudio));
                continue;
            }
            var document = await library.OpenAsync(documentId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A queued document is missing from the library.");
            if (document.CurrentRevisionId != item.RevisionId)
                throw new InvalidOperationException("A queued document was edited after it was added. Remove it and add the file again.");
            items.Add(await PlanItemAsync(batch, profile, engines, la, laUnavailable, item, document, cancellationToken).ConfigureAwait(false));
        }
        var consentItems = items.Where(x => x.Engine is not null)
            .Select(x => new BatchConsentItem(x.ItemId, x.SourceSha256, x.DocumentId, x.RevisionId, x.PlanSha256, x.EstimatedMaximumMinorUnits, x.Engine!, x.LongAudio))
            .ToArray();
        var consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.ReviewedInstall, profile, accountStableId, projectStableId, consentItems, clock.GetUtcNow());
        return new(BatchAdmissionRequest.CurrentFormat, batch.BatchId, profile.Sha256(), batch.SettingsSha256, accountStableId, projectStableId,
            batch.Settings.LanguageCode, batch.Settings.VoiceStableId, profile.Currency, profile.Scale, items, consent.TotalMaximumMinorUnits, consent,
            la is null ? null : checked(la.HeadroomUsdCents + retainedLongAudio), la?.CapUsdCents);
    }

    private async Task<BatchAdmissionRequestItem> PlanItemAsync(NarrationBatch batch, BatchAuthorityProfile profile, string[] engines,
        BatchLongAudioPlanner.Context? la, string? laUnavailable, BatchItem item, DocumentSnapshot document, CancellationToken token)
    {
        string? why = null;
        if (engines.Contains(BatchEngines.LongAudio, StringComparer.Ordinal))
        {
            var plan = la is null ? BatchLongAudioPlan.No(laUnavailable ?? "Long Audio is not available.") : BatchLongAudioPlanner.Plan(la, batch, item, document.Text);
            if (plan.Approval is { } approval)
                return new(item.ItemId, item.Order, item.SourceSha256, item.Characters, document.Id, item.RevisionId!.Value, BatchEngines.LongAudio,
                    approval.RequestBindingSha256, 1, approval.FamilyMaximumUsdCents, true, false,
                    approval.AdaptiveRecovery ? "Whole file in one Long Audio request; room for one split if Google reports a sentence too long." : "Whole file in one Long Audio request.",
                    null, approval);
            why = plan.Unsupported;
        }
        if (!engines.Contains(BatchEngines.CompleteArticle, StringComparer.Ordinal))
            return new(item.ItemId, item.Order, item.SourceSha256, item.Characters, document.Id, item.RevisionId!.Value, null, string.Empty, 0, 0, false, false,
                (why ?? "No allowed engine can take this file.") + " Remove it, or allow the ordinary engine in the batch profile.", null, null);
        var compiled = ChirpArticlePlanCompiler.CompileSentences(document, batch.Settings.LanguageCode, batch.Settings.VoiceStableId);
        long maximum = profile.EstimateMinorUnits(compiled.SourceScalars);
        bool exact = false, needsAdmission = true;
        if (articles is not null)
        {
            try
            {
                var review = await articles.PrepareAsync(document.Id, token).ConfigureAwait(false);
                if (string.Equals(review.PlanSha256, compiled.PlanSha256, StringComparison.Ordinal)
                    && string.Equals(review.Currency, profile.Currency, StringComparison.Ordinal) && review.Scale == profile.Scale)
                { maximum = review.MaximumAdditionalMinorUnits; exact = true; needsAdmission = false; }
            }
            catch (InvalidOperationException) { needsAdmission = true; }
            catch (InvalidDataException) { needsAdmission = true; }
        }
        string note = (why is null ? string.Empty : why + " Using the ordinary engine. ")
            + (needsAdmission ? "Needs an installed article admission before it can run (nothing is sent until then)." : "Admitted; the maximum is the service's exact review.");
        return new(item.ItemId, item.Order, item.SourceSha256, item.Characters, document.Id, item.RevisionId!.Value, BatchEngines.CompleteArticle,
            compiled.PlanSha256, compiled.Parts.Count, maximum, exact, needsAdmission, note, compiled, null);
    }

    /// <summary>Writes the request to a new file; never overwrites. Returns the path written.</summary>
    public static async Task<string> WriteAsync(BatchAdmissionRequest request, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string path = BatchManifestWriter.FreePath(folder, $"batch-admission-request-{request.BatchId:N}", ".json");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
            await JsonSerializer.SerializeAsync(stream, request, InstalledBatchAuthorityStore.Json, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
