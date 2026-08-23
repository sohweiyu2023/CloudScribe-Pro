using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5SegmentSchedulerTests
{
    [Fact]
    public async Task AcceptedSegmentsPersistCompletionAndSkipProviderAfterRestart()
    {
        using var temp = new TemporaryDirectory();
        var provider = new DeterministicFakeGenerationProvider();
        var cache = new FileGenerationSegmentCache(Path.Combine(temp.Path, "cache"));
        var store = new AtomicJsonGenerationSegmentProgressStore(Path.Combine(temp.Path, "progress"));
        var scheduler = CreateScheduler(provider, cache, store);
        var segment = CreateSegment(Guid.NewGuid(), "segment-01", 0, "idem-01", "hello");

        var first = await scheduler.ExecuteReadyAsync([segment]);
        Assert.Equal(GenerationSegmentProgressState.Completed, first[0].Progress.State);
        Assert.Equal(1, provider.PhysicalSubmissionCount);

        var restartedScheduler = CreateScheduler(provider, cache, new AtomicJsonGenerationSegmentProgressStore(Path.Combine(temp.Path, "progress")));
        var second = await restartedScheduler.ExecuteReadyAsync([segment]);

        Assert.Equal(GenerationSegmentProgressState.Completed, second[0].Progress.State);
        Assert.Equal(1, provider.PhysicalSubmissionCount);
    }

    [Fact]
    public async Task SubmissionUnknownNeverAutomaticallyDuplicatesAfterRestart()
    {
        using var temp = new TemporaryDirectory();
        var provider = new DeterministicFakeGenerationProvider(FakeGenerationOutcome.SubmissionUnknown);
        var cache = new FileGenerationSegmentCache(Path.Combine(temp.Path, "cache"));
        var progressDirectory = Path.Combine(temp.Path, "progress");
        var segment = CreateSegment(Guid.NewGuid(), "segment-unknown", 0, "idem-unknown", "ambiguous");

        var firstScheduler = CreateScheduler(provider, cache, new AtomicJsonGenerationSegmentProgressStore(progressDirectory));
        var first = await firstScheduler.ExecuteReadyAsync([segment]);
        Assert.Equal(GenerationSegmentProgressState.SubmissionUnknown, first[0].Progress.State);
        Assert.Equal(1, provider.PhysicalSubmissionCount);

        var restartedScheduler = CreateScheduler(provider, cache, new AtomicJsonGenerationSegmentProgressStore(progressDirectory));
        var second = await restartedScheduler.ExecuteReadyAsync([segment]);

        Assert.Equal(GenerationSegmentProgressState.SubmissionUnknown, second[0].Progress.State);
        Assert.Equal(1, provider.PhysicalSubmissionCount);
    }

    [Fact]
    public async Task PersistedSubmittingStateIsReconciledInsteadOfResubmitted()
    {
        using var temp = new TemporaryDirectory();
        var provider = new DeterministicFakeGenerationProvider();
        var cache = new FileGenerationSegmentCache(Path.Combine(temp.Path, "cache"));
        var store = new AtomicJsonGenerationSegmentProgressStore(Path.Combine(temp.Path, "progress"));
        var segment = CreateSegment(Guid.NewGuid(), "segment-crash-window", 2, "idem-crash", "crash-window");
        var progress = new GenerationSegmentProgress(
            segment.JobId,
            segment.SegmentId,
            segment.SegmentIndex,
            segment.Request.IdempotencyKey,
            GenerationSegmentProgressState.Submitting,
            1,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await store.SaveAsync(progress);

        var scheduler = CreateScheduler(provider, cache, store);
        var result = await scheduler.ExecuteReadyAsync([segment]);

        Assert.Equal(GenerationSegmentProgressState.SubmissionUnknown, result[0].Progress.State);
        Assert.Equal(0, provider.PhysicalSubmissionCount);
    }

    [Fact]
    public async Task LongJobResumesOnlyIncompleteSegmentsAfterProcessRestart()
    {
        using var temp = new TemporaryDirectory();
        var provider = new DeterministicFakeGenerationProvider();
        var cache = new FileGenerationSegmentCache(Path.Combine(temp.Path, "cache"));
        var progressDirectory = Path.Combine(temp.Path, "progress");
        var jobId = Guid.NewGuid();
        var segments = Enumerable.Range(0, 40)
            .Select(index => CreateSegment(jobId, $"segment-{index:D2}", index, $"idem-{index:D2}", $"payload-{index:D2}"))
            .ToArray();

        var firstStore = new AtomicJsonGenerationSegmentProgressStore(progressDirectory);
        var firstScheduler = CreateScheduler(provider, cache, firstStore);
        await firstScheduler.ExecuteReadyAsync(segments[..20]);
        Assert.Equal(20, provider.PhysicalSubmissionCount);

        var restartedStore = new AtomicJsonGenerationSegmentProgressStore(progressDirectory);
        var restartedScheduler = CreateScheduler(provider, cache, restartedStore);
        var resumed = await restartedScheduler.ExecuteReadyAsync(segments);

        Assert.All(resumed, static result => Assert.Equal(GenerationSegmentProgressState.Completed, result.Progress.State));
        Assert.Equal(40, provider.PhysicalSubmissionCount);
        Assert.Equal(40, (await restartedStore.ListForJobAsync(jobId)).Count);
    }

    private static GenerationSegmentScheduler CreateScheduler(
        DeterministicFakeGenerationProvider provider,
        IGenerationSegmentCache cache,
        IGenerationSegmentProgressStore progressStore)
    {
        var executor = new GenerationSegmentExecutor(provider, cache, new DeterministicGenerationPrivateCacheKeyProvider());
        var policy = new GenerationExecutionPolicy(3, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2), 4);
        return new GenerationSegmentScheduler(executor, cache, progressStore, policy);
    }

    private static GenerationScheduledSegment CreateSegment(Guid jobId, string segmentId, int index, string idempotencyKey, string text)
    {
        var request = new GenerationSegmentExecutionRequest(
            "cloudscribe.fake.deterministic",
            "synthesize-speech",
            "account-1",
            "voice-1",
            "profile-1",
            idempotencyKey,
            Encoding.UTF8.GetBytes(text),
            "wav",
            CreateTrustContext());
        return new GenerationScheduledSegment(jobId, segmentId, index, request);
    }

    private static GenerationCacheTrustContext CreateTrustContext() => new(
        "cloudscribe.fake.deterministic", "account-1", "project-test", "fake-endpoint", "local",
        "synthesize-speech", "fake-model-v1", "voice-1", "stock-fake-voice", "speech-plan-v1", "en-SG",
        "controls-default", "wav", "pcm16", "fake-adapter-v1", "compiler-v1", "ast-v1", "normalize-v1",
        "pricing-v2.23-test", "capabilities-v1", "governance-test", "features-test", "account-capabilities-test");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudscribe-stage5-scheduler-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
