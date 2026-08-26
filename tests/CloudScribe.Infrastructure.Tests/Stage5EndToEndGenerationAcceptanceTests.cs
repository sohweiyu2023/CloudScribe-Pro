using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage5EndToEndGenerationAcceptanceTests
{
    [Fact]
    public async Task CanonicalPlanFlowsThroughApprovalGenerationCacheAndProofPass()
    {
        var root = CreateScratchDirectory();
        try
        {
            var segments = CreateCanonicalSegments();
            var (collectionId, itemEstimates) = CreateApprovedEstimate(segments);
            var provider = new DeterministicFakeGenerationProvider();
            var executor = CreateExecutor(provider, new FileGenerationSegmentCache(root));

            var proofInputs = await GenerateSegmentsAsync(
                provider,
                executor,
                segments,
                collectionId,
                itemEstimates,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(segments.Count, provider.PhysicalSubmissionCount);
            await VerifyCachedSegmentsAsync(
                provider,
                executor,
                segments,
                collectionId,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(segments.Count, provider.PhysicalSubmissionCount);
            AssertProofPass(proofInputs, segments.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DurableSchedulerSurvivesRestartWithoutResubmittingCompletedSegments()
    {
        var root = CreateScratchDirectory();
        try
        {
            var cacheDirectory = Path.Combine(root, "cache");
            var progressDirectory = Path.Combine(root, "progress");
            var jobId = Guid.NewGuid();
            var provider = new DeterministicFakeGenerationProvider();
            var policy = new GenerationExecutionPolicy(
                maximumAttempts: 3,
                initialBackoff: TimeSpan.FromMilliseconds(10),
                maximumBackoff: TimeSpan.FromSeconds(1),
                maximumConcurrentRequests: 3);
            var scheduled = CreateScheduledSegments(jobId, provider.ProviderStableId);

            var firstCache = new FileGenerationSegmentCache(cacheDirectory);
            var firstStore = new AtomicJsonGenerationSegmentProgressStore(progressDirectory);
            var firstScheduler = new GenerationSegmentScheduler(
                CreateExecutor(provider, firstCache), firstCache, firstStore, policy);
            var firstRun = await firstScheduler.ExecuteReadyAsync(
                scheduled,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(12, firstRun.Count);
            Assert.All(firstRun, static result => Assert.Equal(GenerationSegmentProgressState.Completed, result.Progress.State));
            Assert.Equal(12, provider.PhysicalSubmissionCount);

            var restartedCache = new FileGenerationSegmentCache(cacheDirectory);
            var restartedStore = new AtomicJsonGenerationSegmentProgressStore(progressDirectory);
            var restartedScheduler = new GenerationSegmentScheduler(
                CreateExecutor(provider, restartedCache), restartedCache, restartedStore, policy);
            var restartedRun = await restartedScheduler.ExecuteReadyAsync(
                scheduled,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var restoredProgress = await restartedStore.ListForJobAsync(
                jobId,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(12, restartedRun.Count);
            Assert.All(restartedRun, static result => Assert.Equal(GenerationSegmentProgressState.Completed, result.Progress.State));
            Assert.All(restartedRun, static result => Assert.Null(result.ExecutionResult));
            Assert.Equal(12, restoredProgress.Count);
            Assert.All(restoredProgress, static progress => Assert.Equal(GenerationSegmentProgressState.Completed, progress.State));
            Assert.Equal(12, provider.PhysicalSubmissionCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AssemblyProofAndDurableReleaseFinalizationFormOneVerifiedPath()
    {
        var root = CreateScratchDirectory();
        try
        {
            var (sourcePath, sourceSha256) = await CreateReleaseSourceAsync(
                root,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var (decision, assemblyPlan, collectionId, segmentId) = CreateReleaseDecision(root, sourcePath, sourceSha256);
            var artifact = await ExecuteAssemblyAsync(
                root,
                assemblyPlan,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var (finalized, checkpointDirectory) = await FinalizeAndAssertReleaseAsync(
                decision,
                artifact.OutputPath,
                segmentId,
                sourceSha256,
                root,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            await AssertRecoveryAndTamperDetectionAsync(
                finalized,
                checkpointDirectory,
                collectionId,
                artifact.OutputPath,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AmbiguousBillableSubmissionReconcilesWithoutSecondPhysicalSubmission()
    {
        var root = CreateScratchDirectory();
        try
        {
            var provider = new DeterministicFakeGenerationProvider(FakeGenerationOutcome.SubmissionUnknown);
            var executor = CreateExecutor(provider, new FileGenerationSegmentCache(root));
            var request = CreateRequest(
                provider.ProviderStableId,
                "ambiguous-idempotency-key",
                Encoding.UTF8.GetBytes("ambiguous payload"));

            var submitted = await executor.ExecuteAsync(
                request,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(submitted.RequiresReconciliation);
            Assert.Equal(1, provider.PhysicalSubmissionCount);

            var reconciled = await executor.ReconcileAsync(
                request,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.NotNull(reconciled);
            Assert.True(reconciled!.RequiresReconciliation);
            Assert.Equal(1, provider.PhysicalSubmissionCount);
            Assert.False(await new FileGenerationSegmentCache(root).ContainsAsync(
                submitted.CacheKey,
                TestContext.Current.CancellationToken).ConfigureAwait(true));

            Assert.True(GenerationJobStateMachine.RequiresReconciliationBeforeAutomaticRetry(GenerationJobState.SubmissionUnknown));
            Assert.False(GenerationJobStateMachine.CanTransition(GenerationJobState.SubmissionUnknown, GenerationJobState.Submitting));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<SpeechPlanSegment> CreateCanonicalSegments()
    {
        var plan = new SpeechPlan(
            "en-SG",
            [
                new SpeechChapter("chapter-1", "Opening"),
                new SpeechVoice("narrator", "voice/en-SG/A"),
                new SpeechText("Hello 👨‍👩‍👧‍👦 Singapore. This is CloudScribe."),
                new SpeechPause(TimeSpan.FromMilliseconds(150)),
                new SpeechPronunciation("CloudScribe", "ipa", "klaʊd skraɪb"),
                new SpeechMark("end"),
                new SpeechTimestampRequest("end"),
            ],
            "document/revision/42");
        var segments = SpeechPlanSegmenter.Segment(
            plan,
            new SpeechSegmentationLimits(18, 512),
            static nodes => Encoding.UTF8.GetByteCount(Compile(nodes)));

        Assert.NotEmpty(segments);
        Assert.Equal(
            "Hello 👨‍👩‍👧‍👦 Singapore. This is CloudScribe.",
            string.Concat(segments.SelectMany(static segment => segment.Nodes)
                .OfType<SpeechText>()
                .Select(static text => text.Text)));
        return segments;
    }

    private static (Guid CollectionId, GenerationItemEstimate[] ItemEstimates) CreateApprovedEstimate(
        IReadOnlyList<SpeechPlanSegment> segments)
    {
        var collectionId = Guid.NewGuid();
        var itemEstimates = segments
            .Select((_, index) => new GenerationItemEstimate(Guid.NewGuid(), index, "USD", 10, 2))
            .ToArray();
        var estimate = new GenerationCollectionEstimate(
            collectionId,
            42,
            DateTimeOffset.UtcNow,
            "USD",
            itemEstimates.Sum(static item => item.ScaledAmount),
            2,
            "pricing/v2.23/test",
            itemEstimates);
        var approval = new GenerationApproval(
            collectionId,
            42,
            "pricing/v2.23/test",
            "USD",
            estimate.ScaledTotal,
            2,
            DateTimeOffset.UtcNow);

        Assert.True(approval.Authorizes(estimate));
        return (collectionId, itemEstimates);
    }

    private static async Task<IReadOnlyList<GenerationProofInput>> GenerateSegmentsAsync(
        DeterministicFakeGenerationProvider provider,
        GenerationSegmentExecutor executor,
        IReadOnlyList<SpeechPlanSegment> segments,
        Guid collectionId,
        IReadOnlyList<GenerationItemEstimate> itemEstimates,
        CancellationToken cancellationToken)
    {
        var proofInputs = new List<GenerationProofInput>();
        foreach (var segment in segments)
        {
            var payload = Encoding.UTF8.GetBytes(Compile(segment.Nodes));
            var request = CreateRequest(
                provider.ProviderStableId,
                $"collection-{collectionId:N}-segment-{segment.Index}",
                payload);
            var result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

            Assert.False(result.CacheHit);
            Assert.Equal(SubmissionDisposition.Accepted, result.Disposition);
            Assert.NotEmpty(result.MediaBytes.ToArray());
            proofInputs.Add(new GenerationProofInput(
                itemEstimates[segment.Index].ItemId,
                MediaValid: true,
                ExpectedDuration: TimeSpan.FromSeconds(1),
                ActualDuration: TimeSpan.FromSeconds(1),
                RequiredTimingMarksPresent: true,
                ProviderDiagnostics: Array.Empty<string>(),
                ProvenanceId: result.CacheKey.PrivateLookupHmacSha256));
        }

        return proofInputs;
    }

    private static async Task VerifyCachedSegmentsAsync(
        DeterministicFakeGenerationProvider provider,
        GenerationSegmentExecutor executor,
        IReadOnlyList<SpeechPlanSegment> segments,
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        foreach (var segment in segments)
        {
            var payload = Encoding.UTF8.GetBytes(Compile(segment.Nodes));
            var cached = await executor.ExecuteAsync(
                CreateRequest(
                    provider.ProviderStableId,
                    $"collection-{collectionId:N}-segment-{segment.Index}",
                    payload),
                cancellationToken).ConfigureAwait(false);

            Assert.True(cached.CacheHit);
            Assert.Equal(SubmissionDisposition.Accepted, cached.Disposition);
        }
    }

    private static void AssertProofPass(IReadOnlyList<GenerationProofInput> proofInputs, int expectedCount)
    {
        var proofPass = new GenerationProofPass();
        var proofResults = proofPass.EvaluateCollection(proofInputs);
        proofPass.EnsureReleaseSafe(proofResults);
        Assert.Equal(expectedCount, proofResults.Count);
        Assert.All(proofResults, static result => Assert.True(result.IsReleaseSafe));
    }

    private static GenerationScheduledSegment[] CreateScheduledSegments(Guid jobId, string providerStableId) =>
        Enumerable.Range(0, 12)
            .Select(index => new GenerationScheduledSegment(
                jobId,
                $"segment-{index:D2}",
                index,
                CreateRequest(
                    providerStableId,
                    $"job-{jobId:N}-segment-{index}",
                    Encoding.UTF8.GetBytes($"payload-{index}"))))
            .ToArray();

    private static async Task<(string SourcePath, string SourceSha256)> CreateReleaseSourceAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.Combine(root, "segments");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "segment.wav");
        var sourceBytes = CreateWave();
        await File.WriteAllBytesAsync(sourcePath, sourceBytes, cancellationToken).ConfigureAwait(false);
        return (sourcePath, Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant());
    }

    private static (
        GenerationCollectionReleaseDecision Decision,
        AudioAssemblyPlan AssemblyPlan,
        Guid CollectionId,
        Guid SegmentId) CreateReleaseDecision(string root, string sourcePath, string sourceSha256)
    {
        var collectionId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var estimate = new GenerationCollectionEstimate(
            collectionId, 42, DateTimeOffset.UtcNow, "USD", 10, 2, "pricing/v2.23/test",
            [new GenerationItemEstimate(segmentId, 0, "USD", 10, 2)]);
        var approval = new GenerationApproval(
            collectionId, 42, "pricing/v2.23/test", "USD", 10, 2, DateTimeOffset.UtcNow);
        var spendAuthorization = new GenerationSpendAuthorization(
            collectionId,
            new AuthorizedSpendCeiling("USD", 10, 2),
            new Dictionary<Guid, AuthorizedSpendCeiling>
            {
                [segmentId] = new AuthorizedSpendCeiling("USD", 10, 2),
            },
            "pricing/v2.23/test",
            42);
        var assemblyPlan = new AudioAssemblyPlan(
            [new AudioSegmentArtifact(segmentId.ToString("D"), sourcePath, "audio/wav", TimeSpan.FromSeconds(1), sourceSha256)],
            new GenerationMasteringProfile("spoken", -1m, -16m, 0, 0),
            ReleaseAudioFormat.Wav,
            TimeSpan.FromMinutes(10),
            Path.Combine(root, "release"),
            "cloudscribe-e2e");
        var proofInputs = new[]
        {
            new GenerationProofInput(
                segmentId, true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), true,
                Array.Empty<string>(), sourceSha256),
        };
        var decision = new GenerationCollectionReleaseCoordinator(
                new GenerationSpendGuard(),
                new GenerationProofPass(),
                new GenerationOutputReservationService(),
                TimeProvider.System)
            .Evaluate(estimate, approval, spendAuthorization, proofInputs, assemblyPlan);

        Assert.True(decision.IsReleaseSafe);
        Assert.Single(decision.OutputReservations);
        return (decision, assemblyPlan, collectionId, segmentId);
    }

    private static async Task<AudioAssemblyExecutionArtifact> ExecuteAssemblyAsync(
        string root,
        AudioAssemblyPlan assemblyPlan,
        CancellationToken cancellationToken)
    {
        var assembly = await new AudioAssemblyNativeExecutor(new WritingNativeTool(CreateWave())).ExecuteAsync(
            assemblyPlan,
            Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"),
            TimeSpan.FromMinutes(1),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var artifact = Assert.Single(assembly.Artifacts);
        Assert.True(File.Exists(artifact.OutputPath));
        return artifact;
    }

    private static async Task<(GenerationReleaseFinalizationResult Finalized, string CheckpointDirectory)> FinalizeAndAssertReleaseAsync(
        GenerationCollectionReleaseDecision decision,
        string outputPath,
        Guid segmentId,
        string sourceSha256,
        string root,
        CancellationToken cancellationToken)
    {
        var checkpointDirectory = Path.Combine(root, "release-checkpoints");
        var finalizer = new DurableGenerationReleaseFinalizer(
            new GenerationReleasePublisher(),
            new GenerationReleaseVerifier(),
            new AtomicJsonGenerationReleaseCheckpointStore(checkpointDirectory));
        var finalized = await finalizer.FinalizeAsync(
            decision,
            "approval-stage5-e2e",
            outputPath,
            [new GenerationPublishedSegment(segmentId, sourceSha256, sourceSha256)],
            cancellationToken).ConfigureAwait(false);

        Assert.True(finalized.IsFinalized);
        Assert.True(finalized.Verification.IsValid);
        Assert.True(finalized.Receipt.Verify());
        return (finalized, checkpointDirectory);
    }

    private static async Task AssertRecoveryAndTamperDetectionAsync(
        GenerationReleaseFinalizationResult finalized,
        string checkpointDirectory,
        Guid collectionId,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var persisted = await new AtomicJsonGenerationReleaseCheckpointStore(checkpointDirectory).ReadAsync(
            collectionId,
            cancellationToken).ConfigureAwait(false);
        Assert.NotNull(persisted);
        Assert.Equal(GenerationReleaseCheckpointState.Finalized, persisted!.State);

        var restartedFinalizer = new DurableGenerationReleaseFinalizer(
            new GenerationReleasePublisher(),
            new GenerationReleaseVerifier(),
            new AtomicJsonGenerationReleaseCheckpointStore(checkpointDirectory));
        var recovered = await restartedFinalizer.RecoverAsync(
            finalized.Receipt,
            cancellationToken).ConfigureAwait(false);
        Assert.True(recovered.IsFinalized);

        await File.AppendAllTextAsync(outputPath, "tampered", cancellationToken).ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => restartedFinalizer.RecoverAsync(finalized.Receipt, cancellationToken)).ConfigureAwait(false);
    }

    private static GenerationSegmentExecutor CreateExecutor(IGenerationProvider provider, IGenerationSegmentCache cache) =>
        new(provider, cache, new DeterministicGenerationPrivateCacheKeyProvider("stage5-end-to-end-v2.23"));

    private static GenerationSegmentExecutionRequest CreateRequest(
        string providerStableId,
        string idempotencyKey,
        ReadOnlyMemory<byte> payload) => new(
            providerStableId,
            "synthesize-speech",
            "account-test",
            "voice/en-SG/A",
            "stage5-e2e-v1",
            idempotencyKey,
            payload,
            "wav",
            CreateTrustContext(providerStableId));

    private static GenerationCacheTrustContext CreateTrustContext(string providerStableId) => new(
        providerStableId, "account-test", "project-test", "endpoint-test", "local", "synthesize-speech",
        "fake-model-snapshot-v1", "voice/en-SG/A", "stock-voice-fingerprint", "speech-plan-v1", "en-SG",
        "controls-stage5-e2e", "wav", "pcm16", "fake-adapter-v1", "stage5-e2e-v1", "ast-v1",
        "normalize-v1", "pricing/v2.23/test", "capabilities-test", "governance-test", "features-test",
        "account-capabilities-test");

    private static string Compile(IEnumerable<SpeechPlanNode> nodes) =>
        string.Join('|', nodes.Select(static node => node switch
        {
            SpeechText text => $"text:{text.Text}",
            SpeechPronunciation pronunciation => $"pron:{pronunciation.Text}:{pronunciation.Alphabet}:{pronunciation.Phonemes}",
            SpeechVoice voice => $"voice:{voice.Role}:{voice.VoiceStableId}",
            SpeechPause pause => $"pause:{pause.Duration.TotalMilliseconds}",
            SpeechMark mark => $"mark:{mark.Name}",
            SpeechTimestampRequest timestamp => $"timestamp:{timestamp.MarkName}",
            SpeechChapter chapter => $"chapter:{chapter.ChapterId}:{chapter.Title}",
            SpeechSpeakerChange speaker => $"speaker:{speaker.SpeakerId}",
            SpeechProsody prosody => $"prosody:{prosody.Rate}:{prosody.PitchSemitones}:{prosody.VolumeDb}",
            SpeechEmphasis emphasis => $"emphasis:{emphasis.Level}",
            _ => throw new InvalidOperationException($"Unsupported speech node type {node.GetType().Name} in acceptance compiler."),
        }));

    private static byte[] CreateWave()
    {
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.GetBytes(36).CopyTo(bytes, 4);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(16_000).CopyTo(bytes, 24);
        BitConverter.GetBytes(32_000).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.GetBytes(0).CopyTo(bytes, 40);
        return bytes;
    }

    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudScribe-Stage5-E2E-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class WritingNativeTool(byte[] payload) : INativeMediaTool
    {
        public async Task<NativeMediaToolResult> RunAsync(
            NativeMediaToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            var output = invocation.Arguments[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, payload, cancellationToken).ConfigureAwait(false);
            return new NativeMediaToolResult(0, false, string.Empty, string.Empty, TimeSpan.FromMilliseconds(1));
        }
    }
}
