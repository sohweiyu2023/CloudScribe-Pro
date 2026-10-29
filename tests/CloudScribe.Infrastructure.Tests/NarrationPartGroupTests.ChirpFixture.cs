using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<ChirpEvidence> ChirpEvidenceAsync(Fixture f, int legacyParts = 60)
    {
        var batch = await f.CreateBatchAsync("Synthetic internal " + Guid.NewGuid().ToString("N"), legacyParts).ConfigureAwait(false);
        await f.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var compiledOld = CompileChirp(batch.Children[0].Text);
        var oldEnvelope = ChirpEnvelope(compiledOld);
        var store = new GoogleGenerationPersistedQueueStateStore(f);
        var clean = new GoogleGenerationPersistedQueueState("synthetic-account", GoogleGenerationProvider.SynthesizeOperationStableId, "synthetic-chirp-historical-" + Guid.NewGuid().ToString("N"), false, null);
        await store.SaveAsync(clean, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var oldClaim = new GoogleGenerationPersistedQueueState(clean.AccountId, clean.OperationStableId, clean.IdempotencyKey, true, null,
            true, Guid.NewGuid().ToString("N"), "google-dispatch-claimed", compiledOld.PayloadSha256, compiledOld.Payload.Length, 1);
        Assert.True(await store.TryClaimSubmissionAsync(oldClaim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        var oldOutcome = oldClaim with { UnresolvedSubmission = false, DiagnosticCode = "google-http-400", ErrorStatus = GoogleErrorStatus.InvalidArgument };
        await store.SaveOutcomeAsync(oldOutcome, TestContext.Current.CancellationToken).ConfigureAwait(false);
        await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).SaveApprovedAsync(
            GoogleGenerationSpendAuthorization.Create(oldEnvelope, "USD", 2, 3, 3), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var rows = await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false);
        List<ChirpHistoricalRange> history = []; int cursor = 0;
        foreach (var child in batch.Children)
        {
            int end = cursor + child.Text.EnumerateRunes().Count();
            history.Add(new(child.Id, child.CurrentRevisionId!.Value, cursor, end, Hash(child.Text), history.Count == 0
                ? [new(clean.IdempotencyKey, JsonHash(rows.Single(x => Equals(x[2], clean.IdempotencyKey))), 3)] : []));
            cursor = end;
        }
        var proposal = ChirpArticlePlanCompiler.Compile(batch.Parent, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        var lineage = ChirpPlanLineageMapper.Map(batch.Parent.Text, proposal.Parts.Select(x => x.Range).ToArray(), history);
        var article = new AdmittedChirpArticle(proposal, lineage, [], proposal.Parts.Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray());
        var baseline = new RecoveryBudgetBaseline(148, 300, 18, 144, new string('a', 64),
            await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false),
            await RowPinsAsync(f, "google_generation_spend_authorizations", 1).ConfigureAwait(false),
            await RowPinsAsync(f, "google_recovery_attempts", 1).ConfigureAwait(false), await RowPinsAsync(f, "google_recovery_grant_transitions", 1).ConfigureAwait(false));
        var clock = new ChirpClock();
        var policy = new ChirpArticleAdmissionPolicy(1, new string('a', 64), new string('b', 64), InstalledRecoveryAdmissionStore.ProfileHash(f.Root),
            clean.AccountId, "synthetic-project", "USD", 2, clock.GetUtcNow().AddHours(1), new string('c', 64), baseline,
            await RowPinsAsync(f, "google_recovery_scopes", 4).ConfigureAwait(false), [], [article], 2, 2);
        var admission = new SyntheticChirpAdmission { Policy = policy };
        return new(batch.Parent, article, admission, clock, new(f, admission, f.Paths, clock), oldOutcome);
    }

    private static GoogleSpeechCompilation CompileChirp(string text) => GoogleSpeechPlanCompiler.Compile(
        new SpeechPlan("cmn-CN", [new SpeechText(text)], "synthetic-chirp"), new("cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3", 5000));
    private static GoogleGenerationSubmissionEnvelope ChirpEnvelope(GoogleSpeechCompilation compiled) => new("synthetic-account", "synthetic-reference",
        "synthetic-capability", "synthetic-price", 1, "cmn-CN-Chirp3-HD-Alnilam", "MP3", compiled.PayloadSha256, compiled.Payload.Length);
    private static ChirpRequestIntent ChirpIntent(ChirpEvidence e, int ordinal = 1)
    {
        var part = e.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal);
        var compiled = CompileChirp(ChirpNarrationPlanner.Slice(e.Document.Text, part.Range));
        return new(e.Document.Id, ordinal, "synthetic-project", ChirpEnvelope(compiled), compiled.Payload, "USD", 2, 1);
    }
    private static ChirpApprovalConfirmation ChirpConfirmation(ChirpRequestReview review) => new(review.PreviewId, review.ReviewSha256, review.MaximumMinorUnits, true, true);
    private static GoogleGenerationPersistedQueueState ChirpClaim(ChirpRequestIntent i, ChirpApprovedAttempt a) => new(i.Envelope.AccountId,
        GoogleGenerationProvider.SynthesizeOperationStableId, a.IdempotencyKey, true, null, true, Guid.NewGuid().ToString("N"),
        "google-dispatch-claimed", i.Envelope.CompiledPayloadSha256, i.Envelope.CompiledPayloadBytes, 1);
    private sealed record ChirpEvidence(DocumentSnapshot Document, AdmittedChirpArticle Article, SyntheticChirpAdmission Admission,
        ChirpClock Clock, ChirpArticleService Service, GoogleGenerationPersistedQueueState Historical);
    private sealed class ChirpClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class SyntheticChirpAdmission : IChirpArticleAdmissionStore
    {
        public ChirpArticleAdmissionPolicy? Policy { get; set; }
        public int Reads { get; set; }
        public int RevokeAt { get; set; } = int.MaxValue;
        public Task<ChirpArticleAdmissionPolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(++Reads >= RevokeAt ? null : Policy);
    }
}
