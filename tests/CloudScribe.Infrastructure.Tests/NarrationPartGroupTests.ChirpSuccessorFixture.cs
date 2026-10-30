using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<ChirpSuccessorFixture> CreateSuccessorFixtureAsync(Fixture fixture, string outcome = "failed")
    {
        var evidence = await ChirpEvidenceAsync(fixture, 1).ConfigureAwait(false);
        var intent = ChirpIntent(evidence);
        var preview = await evidence.Service.PrepareAsync(intent, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var parent = await evidence.Service.ApproveAsync(ChirpConfirmation(preview), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var claim = ChirpClaim(intent, parent);
        Assert.True(await evidence.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = parent.AuthorizationId },
            intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        var observed = claim with
        {
            UnresolvedSubmission = string.Equals(outcome, "uncertain", StringComparison.Ordinal),
            DiagnosticCode = string.Equals(outcome, "accepted", StringComparison.Ordinal) ? "google-accepted" : "google-http-400",
            ErrorStatus = string.Equals(outcome, "accepted", StringComparison.Ordinal) ? GoogleErrorStatus.NotProvided : GoogleErrorStatus.InvalidArgument,
        };
        await new GoogleGenerationPersistedQueueStateStore(fixture, evidence.Admission).SaveOutcomeAsync(observed, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var rows = await FinancialRowsAsync(fixture, "chirp_article_attempts").ConfigureAwait(false);
        var queues = await FinancialRowsAsync(fixture, "google_generation_queue_states").ConfigureAwait(false);
        var grant = new ChirpArticleSuccessorGrant(Guid.NewGuid(), parent.AuthorizationId, parent.AuthorizationId,
            evidence.Article.Proposal.PlanSha256, 1, parent.IdempotencyKey, JsonHash(Assert.Single(rows)),
            JsonHash(queues.Single(x => Equals(x[2], parent.IdempotencyKey))), claim.LocalAttemptId!, intent.Envelope.CredentialReferenceId,
            new string('d', 64), 1, 1);
        var original = evidence.Admission.Policy!;
        evidence.Admission.Policy = original with
        {
            SchemaVersion = 2,
            SourceApprovalSha256 = new string('e', 64),
            Budget = original.Budget with
            {
                ProjectCommittedMinorUnits = 149,
                AllocationCommittedMinorUnits = 19,
                QueueRows = await RowPinsAsync(fixture, "google_generation_queue_states", 3).ConfigureAwait(false),
            },
            PriorArticleAttemptRows = await RowPinsAsync(fixture, "chirp_article_attempts", 1).ConfigureAwait(false),
            PriorSuccessorAttemptRows = [],
            SuccessorGrant = grant,
            Articles = [evidence.Article with { AllowedSegments = [new(1, 1)] }],
            MaximumNewAttempts = 1,
            MaximumAdditionalReservationMinorUnits = 1,
        };
        return new(evidence, intent, parent, observed, JsonHash(rows));
    }

    private sealed record ChirpSuccessorFixture(ChirpEvidence Evidence, ChirpRequestIntent Intent, ChirpApprovedAttempt Parent,
        GoogleGenerationPersistedQueueState FailedState, string ParentRowsSha256);
}
