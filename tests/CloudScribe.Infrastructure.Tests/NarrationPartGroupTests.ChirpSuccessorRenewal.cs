using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("same-approval")]
    [InlineData("same-grant")]
    [InlineData("uppercase-approval")]
    [InlineData("new-explicit-approval")]
    public async Task FailedSuccessorCannotRenewItsOwnGrantOrSourceApproval(string renewal)
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var state = await CreateSuccessorFixtureAsync(fixture);
        var service = state.Evidence.Service;
        var review = await service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        var child = await service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var claim = ChirpClaim(state.Intent, child);
        Assert.True(await service.TryClaimAuthorizedAsync(state.Intent.Envelope with { ChirpAuthorizationId = child.AuthorizationId },
            state.Intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken));
        await new GoogleGenerationPersistedQueueStateStore(fixture, state.Evidence.Admission).SaveOutcomeAsync(claim with
        { UnresolvedSubmission = false, DiagnosticCode = "google-http-400", ErrorStatus = GoogleErrorStatus.InvalidArgument }, TestContext.Current.CancellationToken);
        var rows = await FinancialRowsAsync(fixture, "chirp_article_successor_attempts");
        var queue = await FinancialRowsAsync(fixture, "google_generation_queue_states");
        var policy = state.Evidence.Admission.Policy!;
        var grant = policy.SuccessorGrant! with
        {
            GrantId = string.Equals(renewal, "same-grant", StringComparison.Ordinal) ? policy.SuccessorGrant!.GrantId : Guid.NewGuid(),
            ParentAuthorizationId = child.AuthorizationId,
            ParentKey = child.IdempotencyKey,
            ParentLocalAttemptId = claim.LocalAttemptId!,
            ParentAttemptRowSha256 = JsonHash(Assert.Single(rows).Take(18).ToArray()),
            ParentQueueRowSha256 = JsonHash(queue.Single(x => Equals(x[2], child.IdempotencyKey))),
        };
        state.Evidence.Admission.Policy = policy with
        {
            SourceApprovalSha256 = renewal switch { "same-approval" => policy.SourceApprovalSha256, "uppercase-approval" => policy.SourceApprovalSha256.ToUpperInvariant(), _ => new string('f', 64) },
            SuccessorGrant = grant,
            PriorSuccessorAttemptRows = await RowPinsAsync(fixture, "chirp_article_successor_attempts", 1),
            Budget = policy.Budget with
            {
                ProjectCommittedMinorUnits = 150,
                AllocationCommittedMinorUnits = 20,
                QueueRows = await RowPinsAsync(fixture, "google_generation_queue_states", 3)
            },
        };
        var restarted = new ChirpArticleService(fixture, state.Evidence.Admission, fixture.Paths, state.Evidence.Clock);
        if (string.Equals(renewal, "new-explicit-approval", StringComparison.Ordinal))
        {
            var fresh = await restarted.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
            await restarted.ApproveAsync(ChirpConfirmation(fresh), TestContext.Current.CancellationToken);
            Assert.Equal(2, (await FinancialRowsAsync(fixture, "chirp_article_successor_attempts")).Count);
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => restarted.PrepareAsync(state.Intent, TestContext.Current.CancellationToken));
            Assert.Single(await FinancialRowsAsync(fixture, "chirp_article_successor_attempts"));
        }
        Assert.Equal(state.ParentRowsSha256, JsonHash(await FinancialRowsAsync(fixture, "chirp_article_attempts")));
    }
}
