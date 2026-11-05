using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SentenceFormatChangesOnlyFailedWireAndReservesOneDistinctSuccessorAfterRestart(bool failedSuccessor)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var state = await SentenceFormatAsync(f, failedSuccessor); var e = state.Evidence;
        if (failedSuccessor)
            TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_SENTENCE_FORMAT_POLICY_BASE64=" +
                Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(e.Admission.Policy)));
        string roots = JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var wire = ChirpArticlePlanCompiler.CompilePart(e.Document.Text, e.Article.Proposal,
            e.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == state.Intent.CoverageOrdinal));
        using var json = JsonDocument.Parse(wire.Payload);
        string ssml = json.RootElement.GetProperty("input").GetProperty("ssml").GetString()!;
        Assert.Equal(ChirpNarrationPlanner.Slice(e.Document.Text,
            e.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == state.Intent.CoverageOrdinal).Range),
            GoogleSpeechPlanCompiler.ReadSentenceMarkedText(ssml));
        Assert.False(json.RootElement.GetProperty("input").TryGetProperty("text", out _));
        var review = await e.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        var child = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        Assert.NotEqual(state.Parent.AuthorizationId, child.AuthorizationId);
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        var repeated = await restarted.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        Assert.Equal(child.AuthorizationId, repeated.ExistingAuthorizationId);
        Assert.Equal(child, await restarted.ApproveAsync(ChirpConfirmation(repeated), TestContext.Current.CancellationToken));
        var claim = ChirpClaim(state.Intent, child);
        Assert.True(await restarted.TryClaimAuthorizedAsync(state.Intent.Envelope with { ChirpAuthorizationId = child.AuthorizationId },
            state.Intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken));
        Assert.False(await restarted.TryClaimAuthorizedAsync(state.Intent.Envelope with { ChirpAuthorizationId = child.AuthorizationId },
            state.Intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken));
        Assert.Equal(roots, JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts")));
        Assert.Equal(failedSuccessor ? 2 : 1, (await FinancialRowsAsync(f, "chirp_article_successor_attempts")).Count);
    }

    [Fact]
    public async Task SentenceFormatFakeAcceptedResultPersistsAndLaterArticleExcludesIt()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var state = await SentenceFormatAsync(f, true);
        var e = state.Evidence;
        var accepted = await AcceptChirpAsync(f, e.Service, state.Intent, 7);
        Assert.Equal(state.Intent.CompiledPayload.ToArray(), accepted.Request.CompiledPayload.ToArray());
        await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        Assert.Equal(3, (await e.Service.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        var p = e.Admission.Policy!;
        var remaining = e.Article.Proposal.Parts.Where(x => x.Range.ReusedReceiptSha256 is null && x.CoverageOrdinal != state.Intent.CoverageOrdinal)
            .Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray();
        e.Admission.Policy = p with
        {
            SchemaVersion = 3,
            SuccessorGrant = null,
            SourceApprovalSha256 = new string('b', 64),
            WholeArticleGeneration = new(Guid.NewGuid(), [e.Article.Proposal.PlanSha256]),
            Articles = [e.Article with { AllowedSegments = remaining }],
            MaximumNewAttempts = remaining.Length,
            MaximumAdditionalReservationMinorUnits = remaining.Length,
            PriorSuccessorAttemptRows = await RowPinsAsync(f, "chirp_article_successor_attempts", 1),
            Budget = p.Budget with
            {
                ProjectCommittedMinorUnits = 154,
                AllocationCommittedMinorUnits = 24,
                ProjectCeilingMinorUnits = 154 + remaining.Length,
                AllocationCeilingMinorUnits = 24 + remaining.Length,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3)
            }
        };
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        var review = await restarted.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(remaining.Length, review.MaximumAdditionalMinorUnits);
        Assert.Equal(3, (await restarted.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
    }

    [Fact]
    public async Task SentenceFormatStoredFifthPlanCannotBeHiddenByReadbackLimit()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var state = await SentenceFormatAsync(f);
        var e = state.Evidence;
        var review = await e.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        _ = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Equal(4, (await FinancialRowsAsync(f, "chirp_article_plans")).Count);
        using (var db = f.CreateDbContext())
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database,
                "INSERT INTO chirp_article_plans SELECT {0},DocumentId,RevisionId,AccountId,ProjectId,ProposalJson,LineageJson,ReusedAudioJson FROM chirp_article_plans LIMIT 1;",
                [new string('f', 64)], TestContext.Current.CancellationToken);
        e.Admission.Policy = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.Equal(5, (await FinancialRowsAsync(f, "chirp_article_plans")).Count);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("old-wire")]
    [InlineData("missing-reuse")]
    [InlineData("unadmitted-format")]
    public async Task SentenceFormatRejectsChangedOrUnadmittedInputsBeforeAnyReservation(string mutation)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var state = await SentenceFormatAsync(f); var e = state.Evidence;
        var intent = state.Intent; var p = e.Admission.Policy!;
        if (string.Equals(mutation, "expired", StringComparison.Ordinal)) e.Clock.Now = p.ExpiresAtUtc;
        if (string.Equals(mutation, "old-wire", StringComparison.Ordinal))
        {
            var old = CompileChirp(ChirpNarrationPlanner.Slice(e.Document.Text,
                e.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == intent.CoverageOrdinal).Range));
            intent = intent with { Envelope = ChirpEnvelope(old), CompiledPayload = old.Payload };
        }
        if (string.Equals(mutation, "missing-reuse", StringComparison.Ordinal)) e.Admission.Policy = p with { Articles = [e.Article with { ReusedAudio = [] }] };
        if (string.Equals(mutation, "unadmitted-format", StringComparison.Ordinal)) e.Admission.Policy = p with { SuccessorGrant = null };
        var error = await Record.ExceptionAsync(() => e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken));
        Assert.True(error is InvalidOperationException or InvalidDataException, error?.ToString());
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_successor_attempts"));
    }

    private static async Task<ChirpSuccessorFixture> SentenceFormatAsync(Fixture f, bool failedSuccessor = false)
    {
        var previous = await SentenceDiagnosticAsync(f).ConfigureAwait(false);
        if (failedSuccessor) previous = await SentenceFormatFailureAsync(f, previous).ConfigureAwait(false);
        var e = previous.Evidence;
        int ordinal = previous.Intent.CoverageOrdinal;
        var oldPart = e.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal);
        string text = ChirpNarrationPlanner.Slice(e.Document.Text, oldPart.Range);
        var grant = e.Admission.Policy!.SuccessorGrant!;
        var metadata = new ChirpFailedPlanRefinement(e.Article.Proposal, ordinal, previous.Parent.AuthorizationId,
            grant.ParentAttemptRowSha256, grant.ParentQueueRowSha256, grant.ParentLocalAttemptId)
        { SentenceMarkers = new(1, [text.EnumerateRunes().Count()]) };
        var proposal = ChirpArticlePlanCompiler.CompileRefined(e.Document, metadata,
            e.Article.ReusedAudio.Select(x => new ChirpNarrationReuse(x.Range.StartScalar, x.Range.EndScalarExclusive,
                x.Range.TextSha256, x.ReceiptSha256)).ToArray());
        var article = e.Article with
        {
            Proposal = proposal,
            AllowedSegments = [new(ordinal, 1)],
            Lineage = ChirpPlanLineageMapper.Map(e.Document.Text,
            proposal.Parts.Select(x => x.Range).ToArray(), e.Article.Lineage.History)
        };
        var policy = e.Admission.Policy;
        e.Admission.Policy = policy with
        {
            SchemaVersion = 2,
            DiagnosticEvidence = null,
            SourceApprovalSha256 = new string('a', 64),
            Articles = [article],
            SuccessorGrant = grant with { GrantId = Guid.NewGuid(), PlanSha256 = proposal.PlanSha256 }
        };
        var wire = ChirpArticlePlanCompiler.CompilePart(e.Document.Text, proposal, proposal.Parts.Single(x => x.CoverageOrdinal == ordinal));
        var intent = previous.Intent with { Envelope = ChirpEnvelope(wire), CompiledPayload = wire.Payload };
        e = e with { Article = article, Service = new(f, e.Admission, f.Paths, e.Clock) };
        return previous with { Evidence = e, Intent = intent };
    }

    private static async Task<ChirpSuccessorFixture> SentenceFormatFailureAsync(Fixture f, ChirpSuccessorFixture previous)
    {
        var e = previous.Evidence;
        var review = await e.Service.PrepareAsync(previous.Intent, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var failed = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var claim = ChirpClaim(previous.Intent, failed);
        Assert.True(await e.Service.TryClaimAuthorizedAsync(previous.Intent.Envelope with { ChirpAuthorizationId = failed.AuthorizationId },
            previous.Intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        var outcome = claim with { DiagnosticCode = "google-http-400", ErrorStatus = GoogleErrorStatus.InvalidArgument, UnresolvedSubmission = false };
        await new GoogleGenerationPersistedQueueStateStore(f, e.Admission).SaveOutcomeAsync(outcome, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var rows = await FinancialRowsAsync(f, "chirp_article_successor_attempts").ConfigureAwait(false);
        var queue = await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false);
        var row = rows.Single(x => Equals(x[0], failed.AuthorizationId.ToString("N")));
        var p = e.Admission.Policy!; var g = p.SuccessorGrant!;
        e.Admission.Policy = p with
        {
            PriorSuccessorAttemptRows = await RowPinsAsync(f, "chirp_article_successor_attempts", 1).ConfigureAwait(false),
            SuccessorGrant = g with
            {
                ParentAuthorizationId = failed.AuthorizationId,
                ParentKey = failed.IdempotencyKey,
                ParentAttemptRowSha256 = JsonHash(row[..18]),
                ParentQueueRowSha256 = JsonHash(queue.Single(x => Equals(x[2], failed.IdempotencyKey))),
                ParentLocalAttemptId = claim.LocalAttemptId!
            },
            Budget = p.Budget with
            {
                ProjectCommittedMinorUnits = 153,
                AllocationCommittedMinorUnits = 23,
                ProjectCeilingMinorUnits = 154,
                AllocationCeilingMinorUnits = 24,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            }
        };
        return previous with { Parent = failed, FailedState = outcome };
    }
}
