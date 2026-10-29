using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<(ChirpEvidence Evidence, string Original)> AddAcceptedChirpReuseAsync(Fixture f, ChirpEvidence e)
    {
        var old = e.Article.Lineage.History[1];
        var child = (await f.Library.OpenAsync(old.DocumentId, TestContext.Current.CancellationToken).ConfigureAwait(false))!;
        var compiled = CompileChirp(child.Text); var envelope = ChirpEnvelope(compiled);
        var store = new GoogleGenerationPersistedQueueStateStore(f);
        var clean = new GoogleGenerationPersistedQueueState(envelope.AccountId, GoogleGenerationProvider.SynthesizeOperationStableId,
            "synthetic-accepted-reuse-" + Guid.NewGuid().ToString("N"), false, null);
        await store.SaveAsync(clean, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var claim = new GoogleGenerationPersistedQueueState(clean.AccountId, clean.OperationStableId, clean.IdempotencyKey, true, null,
            true, Guid.NewGuid().ToString("N"), "google-dispatch-claimed", compiled.PayloadSha256, compiled.Payload.Length, 1);
        Assert.True(await store.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        await store.SaveOutcomeAsync(claim with { UnresolvedSubmission = false, DiagnosticCode = "google-accepted" }, TestContext.Current.CancellationToken).ConfigureAwait(false);
        await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).SaveApprovedAsync(
            GoogleGenerationSpendAuthorization.Create(envelope, "USD", 2, 3, 3), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var initial = new ChirpNarrationReuse(old.StartScalar, old.EndScalarExclusive, old.TextSha256, new string('a', 64));
        var preview = ChirpArticlePlanCompiler.Compile(e.Document, "cmn-CN", envelope.VoiceName, [initial]);
        int reusedOrdinal = preview.Parts.Single(x => x.Range.ReusedReceiptSha256 is not null).CoverageOrdinal;
        byte[] media = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(media, 0); media[10] = checked((byte)reusedOrdinal);
        string original = Path.Combine(f.Root, "accepted-original-" + Guid.NewGuid().ToString("N") + ".mp3");
        await File.WriteAllBytesAsync(original, media, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var oldExporter = new ArticleAudioExportService(f, f.Library, store, f.Paths, new ArticleNative());
        await oldExporter.RecordAcceptedAsync(child, new(GoogleGenerationProvider.StableProviderId, clean.OperationStableId, clean.AccountId,
            clean.IdempotencyKey, compiled.Payload, "mp3"), new(new(true, true, "synthetic-accepted"),
            new(SubmissionDisposition.Accepted, null, media, "audio/mpeg", null, "google-accepted")), original, TestContext.Current.CancellationToken).ConfigureAwait(false);
        byte[] receiptBytes = await File.ReadAllBytesAsync(Path.Combine(f.Root, "accepted-article-audio", claim.LocalAttemptId + ".json"), TestContext.Current.CancellationToken).ConfigureAwait(false);
        string receiptHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(receiptBytes));
        var audio = JsonSerializer.Deserialize<ArticleAcceptedAudio>(receiptBytes)!;
        var plan = ChirpArticlePlanCompiler.Compile(e.Document, "cmn-CN", envelope.VoiceName, [initial with { ReceiptSha256 = receiptHash }]);
        var history = e.Article.Lineage.History.ToArray();
        var rows = await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false);
        history[1] = old with { Attempts = [new(clean.IdempotencyKey, JsonHash(rows.Single(x => Equals(x[2], clean.IdempotencyKey))), 3)] };
        var lineage = ChirpPlanLineageMapper.Map(e.Document.Text, plan.Parts.Select(x => x.Range).ToArray(), history);
        var kept = plan.Parts.Single(x => x.Range.ReusedReceiptSha256 is not null);
        var article = new AdmittedChirpArticle(plan, lineage, [new(e.Document.Id, e.Document.CurrentRevisionId!.Value, receiptHash, kept.Range, audio)],
            plan.Parts.Where(x => x.Range.ReusedReceiptSha256 is null).Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray());
        e.Admission.Policy = e.Admission.Policy! with
        {
            Articles = [article],
            Budget = e.Admission.Policy.Budget with
            { QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false), SpendRows = await RowPinsAsync(f, "google_generation_spend_authorizations", 1).ConfigureAwait(false) },
        };
        return (e with { Article = article }, original);
    }
}
