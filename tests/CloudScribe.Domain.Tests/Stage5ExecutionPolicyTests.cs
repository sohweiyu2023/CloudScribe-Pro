using System.Text;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5ExecutionPolicyTests
{
    [Fact]
    public void AmbiguousSubmissionNeverRetriesAutomatically()
    {
        var policy = new GenerationExecutionPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 3);
        var decision = policy.DecideRetry(GenerationJobState.SubmissionUnknown, SubmissionDisposition.UnknownRequiresReconciliation, 1, null, 42);
        Assert.False(decision.MayRetryAutomatically);
        Assert.Contains("reconciled", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedBillableSubmissionNeverDuplicatesAutomatically()
    {
        var policy = new GenerationExecutionPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 3);
        var decision = policy.DecideRetry(GenerationJobState.Running, SubmissionDisposition.Accepted, 1, null, 42);
        Assert.False(decision.MayRetryAutomatically);
        Assert.Contains("duplicated", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryAfterIsHonoredAndBounded()
    {
        var policy = new GenerationExecutionPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 3);
        var decision = policy.DecideRetry(GenerationJobState.RateLimited, SubmissionDisposition.RejectedSafeToRetry, 1, TimeSpan.FromMinutes(10), 0);
        Assert.True(decision.MayRetryAutomatically);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.Delay);
    }

    [Fact]
    public void ExponentialBackoffIsDeterministicAndBounded()
    {
        var policy = new GenerationExecutionPolicy(8, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20), 3);
        var first = policy.DecideRetry(GenerationJobState.RetryWait, SubmissionDisposition.RejectedSafeToRetry, 3, null, 123);
        var second = policy.DecideRetry(GenerationJobState.RetryWait, SubmissionDisposition.RejectedSafeToRetry, 3, null, 123);
        Assert.True(first.MayRetryAutomatically);
        Assert.Equal(first.Delay, second.Delay);
        Assert.InRange(first.Delay, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void PrivateCacheLookupChangesWithSemanticInputs()
    {
        var hmacKey = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var payload = Encoding.UTF8.GetBytes("<speak>Hello 👨‍👩‍👧‍👦</speak>");
        var baselineContext = CreateTrustContext("voice-a");
        var baseline = PrivateCacheLookupKey.Derive(hmacKey, baselineContext, payload);
        var same = PrivateCacheLookupKey.Derive(hmacKey, baselineContext, payload);
        var otherVoice = PrivateCacheLookupKey.Derive(hmacKey, CreateTrustContext("voice-b"), payload);
        Assert.Equal(baseline, same);
        Assert.NotEqual(baseline, otherVoice);
        Assert.Equal(64, baseline.HmacSha256.Length);
    }

    [Fact]
    public void RestartDuringSubmittingRequiresReconciliation()
    {
        var snapshot = new GenerationRecoverySnapshot(Guid.NewGuid(), GenerationJobState.Submitting, 1, 10, 3, null, 1);
        Assert.Equal(GenerationRecoveryKind.Reconcile, snapshot.DecideRecovery().Kind);
    }

    [Fact]
    public void RestartDuringRunningRequeuesFromDurableState()
    {
        var snapshot = new GenerationRecoverySnapshot(
            Guid.NewGuid(), GenerationJobState.Running, 1, 10, 3,
            new GenerationSubmissionRecord("idem-1", SubmissionDisposition.Accepted, "provider-123", 1), 1);
        Assert.Equal(GenerationRecoveryKind.Requeue, snapshot.DecideRecovery().Kind);
    }

    [Fact]
    public void AmbiguousSubmissionRecordForcesRecoveryReconciliation()
    {
        var snapshot = new GenerationRecoverySnapshot(
            Guid.NewGuid(), GenerationJobState.AbandonedRecoverable, 2, 0, 4,
            new GenerationSubmissionRecord("idem-2", SubmissionDisposition.UnknownRequiresReconciliation, null, 1), 1);
        Assert.Equal(GenerationRecoveryKind.Reconcile, snapshot.DecideRecovery().Kind);
    }

    [Fact]
    public void ConcurrencyGateNeverExceedsConfiguredBound()
    {
        var gate = new GenerationConcurrencyGate(2);
        Assert.True(gate.TryAcquire());
        Assert.True(gate.TryAcquire());
        Assert.False(gate.TryAcquire());
        Assert.Equal(2, gate.ActiveCount);
        gate.Release();
        Assert.True(gate.TryAcquire());
        Assert.Equal(2, gate.ActiveCount);
    }

    private static GenerationCacheTrustContext CreateTrustContext(string voice) => new(
        "google", "account-a", "project-a", "endpoint-a", "us-central1", "synthesize",
        "model-a", voice, "stock-fingerprint", "speech-plan-v1", "en-SG", "controls-a", "wav",
        "pcm16", "adapter-v1", "compiler-v1", "ast-v1", "normalizer-v1", "pricing-a",
        "capabilities-a", "governance-a", "features-a", "account-capabilities-a");
}
