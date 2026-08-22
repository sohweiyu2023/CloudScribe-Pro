using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5ExecutionPolicyTests
{
    [Fact]
    public void AmbiguousSubmissionNeverRetriesAutomatically()
    {
        var policy = new GenerationExecutionPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 3);

        var decision = policy.DecideRetry(
            GenerationJobState.SubmissionUnknown,
            SubmissionDisposition.UnknownRequiresReconciliation,
            completedAttempts: 1,
            retryAfter: null,
            deterministicJitterSeed: 42);

        Assert.False(decision.MayRetryAutomatically);
        Assert.Contains("reconciled", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedBillableSubmissionNeverDuplicatesAutomatically()
    {
        var policy = new GenerationExecutionPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 3);

        var decision = policy.DecideRetry(
            GenerationJobState.Running,
            SubmissionDisposition.Accepted,
            completedAttempts: 1,
            retryAfter: null,
            deterministicJitterSeed: 42);

        Assert.False(decision.MayRetryAutomatically);
        Assert.Contains("duplicated", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryAfterIsHonoredAndBounded()
    {
        var policy = new GenerationExecutionPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 3);

        var decision = policy.DecideRetry(
            GenerationJobState.RateLimited,
            SubmissionDisposition.RejectedSafeToRetry,
            completedAttempts: 1,
            retryAfter: TimeSpan.FromMinutes(10),
            deterministicJitterSeed: 0);

        Assert.True(decision.MayRetryAutomatically);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.Delay);
    }

    [Fact]
    public void ExponentialBackoffIsDeterministicAndBounded()
    {
        var policy = new GenerationExecutionPolicy(8, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20), 3);

        var first = policy.DecideRetry(
            GenerationJobState.RetryWait,
            SubmissionDisposition.RejectedSafeToRetry,
            completedAttempts: 3,
            retryAfter: null,
            deterministicJitterSeed: 123);
        var second = policy.DecideRetry(
            GenerationJobState.RetryWait,
            SubmissionDisposition.RejectedSafeToRetry,
            completedAttempts: 3,
            retryAfter: null,
            deterministicJitterSeed: 123);

        Assert.True(first.MayRetryAutomatically);
        Assert.Equal(first.Delay, second.Delay);
        Assert.InRange(first.Delay, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void ContentAddressedCacheKeyChangesWithSemanticInputs()
    {
        var payload = "<speak>Hello 👨‍👩‍👧‍👦</speak>"u8;
        var baseline = ContentAddressedSegmentKey.Create(payload, "google", "synthesize", "voice-a", "profile-1");
        var same = ContentAddressedSegmentKey.Create(payload, "google", "synthesize", "voice-a", "profile-1");
        var otherVoice = ContentAddressedSegmentKey.Create(payload, "google", "synthesize", "voice-b", "profile-1");

        Assert.Equal(baseline, same);
        Assert.NotEqual(baseline, otherVoice);
        Assert.Equal(64, baseline.Sha256.Length);
    }

    [Fact]
    public void RestartDuringSubmittingRequiresReconciliation()
    {
        var snapshot = new GenerationRecoverySnapshot(
            Guid.NewGuid(),
            GenerationJobState.Submitting,
            AttemptCount: 1,
            Priority: 10,
            Revision: 3,
            LastSubmission: null,
            UpdatedAtUnixMilliseconds: 1);

        var action = snapshot.DecideRecovery();

        Assert.Equal(GenerationRecoveryKind.Reconcile, action.Kind);
    }

    [Fact]
    public void RestartDuringRunningRequeuesFromDurableState()
    {
        var snapshot = new GenerationRecoverySnapshot(
            Guid.NewGuid(),
            GenerationJobState.Running,
            AttemptCount: 1,
            Priority: 10,
            Revision: 3,
            LastSubmission: new GenerationSubmissionRecord("idem-1", SubmissionDisposition.Accepted, "provider-123", 1),
            UpdatedAtUnixMilliseconds: 1);

        var action = snapshot.DecideRecovery();

        Assert.Equal(GenerationRecoveryKind.Requeue, action.Kind);
    }

    [Fact]
    public void AmbiguousSubmissionRecordForcesRecoveryReconciliation()
    {
        var snapshot = new GenerationRecoverySnapshot(
            Guid.NewGuid(),
            GenerationJobState.AbandonedRecoverable,
            AttemptCount: 2,
            Priority: 0,
            Revision: 4,
            LastSubmission: new GenerationSubmissionRecord("idem-2", SubmissionDisposition.UnknownRequiresReconciliation, null, 1),
            UpdatedAtUnixMilliseconds: 1);

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
}
