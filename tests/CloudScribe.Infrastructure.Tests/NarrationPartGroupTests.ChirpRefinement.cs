using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task RefinedPilotRetainsFailedHoldAndReusesAcceptedReceiptAfterRestart()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var fixture = await RefinedEvidenceAsync(f);
        string rootsBefore = JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var service = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        var progress = await service.ProgressAsync(fixture.Evidence.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, progress.SavedCoverageParts);
        Assert.False(progress.Complete);
        int pilot = fixture.Article.AllowedSegments[0].CoverageOrdinal;
        var resolved = await service.ResolveSegmentAsync(fixture.Evidence.Document.Id, pilot, "synthetic-account", "synthetic-project",
            "cmn-CN-Chirp3-HD-Alnilam", "cmn-CN", TestContext.Current.CancellationToken);
        var compiled = CompileChirp(resolved.ExactText);
        var intent = new ChirpRequestIntent(resolved.Document.Id, pilot, "synthetic-project", ChirpEnvelope(compiled), compiled.Payload, "USD", 2, 1);
        var accepted = await AcceptChirpAsync(f, service, intent, 3);
        await service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await service.ProgressAsync(resolved.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_plans")).Count);
        Assert.Equal(3, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        Assert.Equal(fixture.AcceptedBytes, await File.ReadAllBytesAsync(fixture.Reuse.Audio.AudioPath, TestContext.Current.CancellationToken));
        Assert.NotEqual(rootsBefore, JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts")), StringComparer.Ordinal);
        fixture.Evidence.Admission.Policy = null;
        var restarted = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        Assert.Equal(2, (await restarted.ProgressAsync(resolved.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        Assert.Equal(fixture.FailedState, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(fixture.FailedState.AccountId,
            fixture.FailedState.OperationStableId, fixture.FailedState.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("missing-reuse")]
    [InlineData("changed-audio")]
    [InlineData("wrong-failure-pin")]
    [InlineData("uncertain-parent")]
    [InlineData("wrong-reuse-plan")]
    [InlineData("wrong-reuse-authorization")]
    [InlineData("wrong-reuse-voice")]
    public async Task RefinedAdmissionRejectsMissingAcceptedWorkTamperAndUncertainty(string mutation)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var fixture = await RefinedEvidenceAsync(f);
        var original = fixture.Evidence.Admission.Policy!;
        if (string.Equals(mutation, "missing-reuse", StringComparison.Ordinal)) fixture.Evidence.Admission.Policy = original with { Articles = [fixture.Article with { ReusedAudio = [] }] };
        if (string.Equals(mutation, "changed-audio", StringComparison.Ordinal)) await File.WriteAllBytesAsync(fixture.Reuse.Audio.AudioPath, [0], TestContext.Current.CancellationToken);
        if (string.Equals(mutation, "wrong-failure-pin", StringComparison.Ordinal)) fixture.Evidence.Admission.Policy = original with
        { Articles = [fixture.Article with { Proposal = fixture.Article.Proposal with { Refinement = fixture.Article.Proposal.Refinement! with { FailedQueueRowSha256 = new string('f', 64) } } }] };
        if (string.Equals(mutation, "uncertain-parent", StringComparison.Ordinal))
        {
            using var db = f.CreateDbContext();
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database,
                "UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE IdempotencyKey={0}", [fixture.FailedState.IdempotencyKey], TestContext.Current.CancellationToken);
        }
        if (mutation.StartsWith("wrong-reuse-", StringComparison.Ordinal))
        {
            var wrong = mutation switch
            {
                "wrong-reuse-plan" => fixture.Reuse with { SourcePlanSha256 = new string('f', 64) },
                "wrong-reuse-authorization" => fixture.Reuse with { SourceAuthorizationId = Guid.NewGuid() },
                _ => fixture.Reuse with { Audio = fixture.Reuse.Audio with { VoiceName = "cmn-CN-Chirp3-HD-Achernar" } }
            };
            fixture.Evidence.Admission.Policy = original with { Articles = [fixture.Article with { ReusedAudio = [wrong] }] };
        }
        var service = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProgressAsync(fixture.Evidence.Document.Id, TestContext.Current.CancellationToken));
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
    }

    private static async Task<RefinedFixture> RefinedEvidenceAsync(Fixture f, bool legacy = false)
    {
        var evidence = legacy ? await LegacyAssociationEvidenceAsync(f).ConfigureAwait(false) : await ChirpEvidenceAsync(f, 10).ConfigureAwait(false);
        if (!legacy)
        {
            var accepted = await AcceptChirpAsync(f, evidence.Service, ChirpIntent(evidence, 2), 2).ConfigureAwait(false);
            await evidence.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        var intent = ChirpIntent(evidence, 1);
        var review = await evidence.Service.PrepareAsync(intent, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var failed = await evidence.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var claim = ChirpClaim(intent, failed);
        Assert.True(await evidence.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = failed.AuthorizationId }, "synthetic-project", "USD", 2, 1, claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        var state = claim with { UnresolvedSubmission = false, DiagnosticCode = "google-http-400", ErrorStatus = GoogleErrorStatus.InvalidArgument };
        await new GoogleGenerationPersistedQueueStateStore(f, evidence.Admission).SaveOutcomeAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var roots = await FinancialRowsAsync(f, "chirp_article_attempts").ConfigureAwait(false);
        var queues = await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false);
        var receipts = await FinancialRowsAsync(f, "chirp_article_receipts").ConfigureAwait(false);
        var receipt = JsonSerializer.Deserialize<ChirpSegmentAcceptedAudio>((string)Assert.Single(receipts)[3]!)!;
        var acceptedRow = roots.Single(x => Equals(x[7], receipt.Audio.IdempotencyKey));
        var reuse = new ChirpReusedArticleAudio(evidence.Document.Id, evidence.Document.CurrentRevisionId!.Value,
            JsonHash(receipt), receipt.Range with { ReusedReceiptSha256 = JsonHash(receipt) }, receipt.Audio,
            evidence.Article.Proposal.PlanSha256, Guid.ParseExact((string)acceptedRow[0]!, "N"));
        var metadata = new ChirpFailedPlanRefinement(evidence.Article.Proposal, 1, failed.AuthorizationId,
            JsonHash(roots.Single(x => Equals(x[0], failed.AuthorizationId.ToString("N")))), JsonHash(queues.Single(x => Equals(x[2], failed.IdempotencyKey))), claim.LocalAttemptId!);
        var proposal = ChirpArticlePlanCompiler.CompileRefined(evidence.Document, metadata,
            [new(reuse.Range.StartScalar, reuse.Range.EndScalarExclusive, reuse.Range.TextSha256, reuse.ReceiptSha256)]);
        var lineage = ChirpPlanLineageMapper.Map(evidence.Document.Text, proposal.Parts.Select(x => x.Range).ToArray(), evidence.Article.Lineage.History);
        var article = new AdmittedChirpArticle(proposal, lineage, [reuse], [new(proposal.Parts.First(x => x.Range.ReusedReceiptSha256 is null).CoverageOrdinal, 1)]);
        var original = evidence.Admission.Policy!;
        evidence.Admission.Policy = original with
        {
            SchemaVersion = 2,
            SourceApprovalSha256 = new string('e', 64),
            Articles = [article],
            Budget = original.Budget with
            {
                ProjectCommittedMinorUnits = 150,
                AllocationCommittedMinorUnits = 20,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            },
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false),
            PriorSuccessorAttemptRows = [],
            LegacyAssociations = original.LegacyAssociations?.Select(x => x with { PlanSha256 = proposal.PlanSha256 }).ToArray(),
            LegacyAssociationRows = original.LegacyAssociations is null ? null : await RowPinsAsync(f, "chirp_article_legacy_associations", 2).ConfigureAwait(false),
            MaximumNewAttempts = 1,
            MaximumAdditionalReservationMinorUnits = 1
        };
        return new(evidence, article, reuse, state, await File.ReadAllBytesAsync(reuse.Audio.AudioPath, TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    private sealed record RefinedFixture(ChirpEvidence Evidence, AdmittedChirpArticle Article, ChirpReusedArticleAudio Reuse,
        GoogleGenerationPersistedQueueState FailedState, byte[] AcceptedBytes);
}
