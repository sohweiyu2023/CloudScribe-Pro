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

            var provider = new DeterministicFakeGenerationProvider();
            var cache = new FileGenerationSegmentCache(root);
            var executor = CreateExecutor(provider, cache);
            var proofInputs = new List<GenerationProofInput>();

            foreach (var segment in segments)
            {
                var payload = Encoding.UTF8.GetBytes(Compile(segment.Nodes));
                var request = CreateRequest(
                    provider.ProviderStableId,
                    $"collection-{collectionId:N}-segment-{segment.Index}",
                    payload);

                var result = await executor.ExecuteAsync(request);
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

            Assert.Equal(segments.Count, provider.PhysicalSubmissionCount);

            foreach (var segment in segments)
            {
                var payload = Encoding.UTF8.GetBytes(Compile(segment.Nodes));
                var cached = await executor.ExecuteAsync(CreateRequest(
                    provider.ProviderStableId,
                    $"collection-{collectionId:N}-segment-{segment.Index}",
                    payload));

                Assert.True(cached.CacheHit);
                Assert.Equal(SubmissionDisposition.Accepted, cached.Disposition);
            }

            Assert.Equal(segments.Count, provider.PhysicalSubmissionCount);

            var proofPass = new GenerationProofPass();
            var proofResults = proofPass.EvaluateCollection(proofInputs);
            proofPass.EnsureReleaseSafe(proofResults);

            Assert.Equal(segments.Count, proofResults.Count);
            Assert.All(proofResults, static result => Assert.True(result.IsReleaseSafe));
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

            var scheduled = Enumerable.Range(0, 12)
                .Select(index => new GenerationScheduledSegment(
                    jobId,
                    $"segment-{index:D2}",
                    index,
                    CreateRequest(
                        provider.ProviderStableId,
                        $"job-{jobId:N}-segment-{index}",
                        Encoding.UTF8.GetBytes($"payload-{index}"))))
                .ToArray();

            var firstCache = new FileGenerationSegmentCache(cacheDirectory);
            var firstStore = new AtomicJsonGenerationSegmentProgressStore(progressDirectory);
            var firstScheduler = new GenerationSegmentScheduler(
                CreateExecutor(provider, firstCache),
                firstCache,
                firstStore,
                policy);

            var firstRun = await firstScheduler.ExecuteReadyAsync(scheduled);

            Assert.Equal(12, firstRun.Count);
            Assert.All(firstRun, static result => Assert.Equal(GenerationSegmentProgressState.Completed, result.Progress.State));
            Assert.Equal(12, provider.PhysicalSubmissionCount);

            var restartedCache = new FileGenerationSegmentCache(cacheDirectory);
            var restartedStore = new AtomicJsonGenerationSegmentProgressStore(progressDirectory);
            var restartedScheduler = new GenerationSegmentScheduler(
                CreateExecutor(provider, restartedCache),
                restartedCache,
                restartedStore,
                policy);

            var restartedRun = await restartedScheduler.ExecuteReadyAsync(scheduled);
            var restoredProgress = await restartedStore.ListForJobAsync(jobId);

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
            var sourceDirectory = Path.Combine(root, "segments");
            var outputDirectory = Path.Combine(root, "release");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(outputDirectory);

            var sourcePath = Path.Combine(sourceDirectory, "segment.wav");
            var sourceBytes = CreateWave();
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();

            var collectionId = Guid.NewGuid();
            var segmentId = Guid.NewGuid();
            var estimate = new GenerationCollectionEstimate(
                collectionId,
                42,
                DateTimeOffset.UtcNow,
                "USD",
                10,
                2,
                "pricing/v2.23/test",
                [new GenerationItemEstimate(segmentId, 0, "USD", 10, 2)]);
            var approval = new GenerationApproval(
                collectionId,
                42,
                "pricing/v2.23/test",
                "USD",
                10,
                2,
                DateTimeOffset.UtcNow);
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
                outputDirectory,
                "cloudscribe-e2e");
            var proofInputs = new[]
            {
                new GenerationProofInput(
                    segmentId,
                    MediaValid: true,
                    ExpectedDuration: TimeSpan.FromSeconds(1),
                    ActualDuration: TimeSpan.FromSeconds(1),
                    RequiredTimingMarksPresent: true,
                    ProviderDiagnostics: Array.Empty<string>(),
                    ProvenanceId: sourceSha256),
            };

            var decision = new GenerationCollectionReleaseCoordinator(
                    new GenerationSpendGuard(),
                    new GenerationProofPass(),
                    new GenerationOutputReservationService(),
                    TimeProvider.System)
                .Evaluate(estimate, approval, spendAuthorization, proofInputs, assemblyPlan);

            Assert.True(decision.IsReleaseSafe);
            Assert.Single(decision.OutputReservations);

            var assembly = await new AudioAssemblyNativeExecutor(new WritingNativeTool(CreateWave())).ExecuteAsync(
                assemblyPlan,
                Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"),
                TimeSpan.FromMinutes(1));
            var artifact = Assert.Single(assembly.Artifacts);
            Assert.True(File.Exists(artifact.OutputPath));

            var checkpointDirectory = Path.Combine(root, "release-checkpoints");
            var finalizer = new DurableGenerationReleaseFinalizer(
                new GenerationReleasePublisher(),
                new GenerationReleaseVerifier(),
                new AtomicJsonGenerationReleaseCheckpointStore(checkpointDirectory));
            var finalized = await finalizer.FinalizeAsync(
                decision,
                "approval-stage5-e2e",
                artifact.OutputPath,
                [new GenerationPublishedSegment(segmentId, sourceSha256, sourceSha256)]);

            Assert.True(finalized.IsFinalized);
            Assert.True(finalized.Verification.IsValid);
            Assert.True(finalized.Receipt.Verify());

            var persisted = await new AtomicJsonGenerationReleaseCheckpointStore(checkpointDirectory).ReadAsync(collectionId);
            Assert.NotNull(persisted);
            Assert.Equal(GenerationReleaseCheckpointState.Finalized, persisted!.State);

            var restartedFinalizer = new DurableGenerationReleaseFinalizer(
                new GenerationReleasePublisher(),
                new GenerationReleaseVerifier(),
                new AtomicJsonGenerationReleaseCheckpointStore(checkpointDirectory));
            var recovered = await restartedFinalizer.RecoverAsync(finalized.Receipt);
            Assert.True(recovered.IsFinalized);

            await File.AppendAllTextAsync(artifact.OutputPath, "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => restartedFinalizer.RecoverAsync(finalized.Receipt));
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

            var submitted = await executor.ExecuteAsync(request);
            Assert.True(submitted.RequiresReconciliation);
            Assert.Equal(1, provider.PhysicalSubmissionCount);

            var reconciled = await executor.ReconcileAsync(request);
            Assert.NotNull(reconciled);
            Assert.True(reconciled!.RequiresReconciliation);
            Assert.Equal(1, provider.PhysicalSubmissionCount);
            Assert.False(await new FileGenerationSegmentCache(root).ContainsAsync(submitted.CacheKey));

            Assert.True(GenerationJobStateMachine.RequiresReconciliationBeforeAutomaticRetry(GenerationJobState.SubmissionUnknown));
            Assert.False(GenerationJobStateMachine.CanTransition(GenerationJobState.SubmissionUnknown, GenerationJobState.Submitting));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            await File.WriteAllBytesAsync(output, payload, cancellationToken);
            return new NativeMediaToolResult(0, false, string.Empty, string.Empty, TimeSpan.FromMilliseconds(1));
        }
    }
}
