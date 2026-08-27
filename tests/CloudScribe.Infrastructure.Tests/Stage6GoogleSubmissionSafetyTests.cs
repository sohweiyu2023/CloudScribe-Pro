using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleSubmissionSafetyTests
{
    [Fact]
    public void AmbiguousAcceptedSubmissionRequiresReconciliation()
    {
        var decision = GoogleSubmissionSafety.Decide(new GoogleSubmissionAttempt(
            "request-1", "idem-1", ProviderAccepted: true, ResponseObserved: false,
            ProviderSupportsSafeIdempotency: true, HttpStatusCode: null, RetryAfter: null));

        Assert.Equal(GoogleSubmissionDisposition.ReconcileRequired, decision.Disposition);
        Assert.Null(decision.Delay);
    }

    [Fact]
    public void TransientFailureRetriesOnlyWithSafeIdempotencyAndBoundedRetryAfter()
    {
        var safe = GoogleSubmissionSafety.Decide(new GoogleSubmissionAttempt(
            "request-2", "idem-2", false, true, true, 429, TimeSpan.FromHours(8)));
        Assert.Equal(GoogleSubmissionDisposition.RetrySafe, safe.Disposition);
        Assert.Equal(TimeSpan.FromHours(1), safe.Delay);

        var unsafeRetry = GoogleSubmissionSafety.Decide(new GoogleSubmissionAttempt(
            "request-3", "idem-3", false, true, false, 503, TimeSpan.FromSeconds(5)));
        Assert.Equal(GoogleSubmissionDisposition.ReconcileRequired, unsafeRetry.Disposition);
    }

    [Fact]
    public void OrdinaryClientErrorFailsWithoutRetry()
    {
        var decision = GoogleSubmissionSafety.Decide(new GoogleSubmissionAttempt(
            "request-4", "idem-4", false, true, true, 400, null));
        Assert.Equal(GoogleSubmissionDisposition.Fail, decision.Disposition);
    }
}
