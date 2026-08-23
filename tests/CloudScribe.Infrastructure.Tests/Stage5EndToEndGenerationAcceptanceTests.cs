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
                "pricing/v2.22/test",
                itemEstimates);
            var approval = new GenerationApproval(
                collectionId,
                42,
                "pricing/v2.22/test",
                "USD",
                estimate.ScaledTotal,
                2,
                DateTimeOffset.UtcNow);

            Assert.True(approval.Authorizes(estimate));

            var provider = new DeterministicFakeGenerationProvider();
            var executor = new GenerationSegmentExecutor(provider, new FileGenerationSegmentCache(root));
            var firstPass = new List<GenerationSegmentExecutionResult>();
            var proofInputs = new List<GenerationProofInput>();

            foreach (var segment in segments)
            {
                var payload = Encoding.UTF8.GetBytes(Compile(segment.Nodes));
                var request = new GenerationSegmentExecutionRequest(
                    provider.ProviderStableId,
                    "synthesize-speech",
                    "account-test",
                    "voice/en-SG/A",
                    "stage5-e2e-v1",
                    $"collection-{collectionId:N}-segment-{segment.Index}",
                    payload,
                    "wav");

                var result = await executor.ExecuteAsync(request);
                Assert.False(result.CacheHit);
                Assert.Equal(SubmissionDisposition.Accepted, result.Disposition);
                Assert.NotEmpty(result.MediaBytes.ToArray());
                firstPass.Add(result);

                proofInputs.Add(new GenerationProofInput(
                    itemEstimates[segment.Index].ItemId,
                    MediaValid: true,
                    ExpectedDuration: TimeSpan.FromSeconds(1),
                    ActualDuration: TimeSpan.FromSeconds(1),
                    RequiredTimingMarksPresent: true,
                    ProviderDiagnostics: Array.Empty<string>(),
                    ProvenanceId: result.CacheKey.Sha256));
            }

            Assert.Equal(segments.Count, provider.PhysicalSubmissionCount);

            foreach (var segment in segments)
            {
                var payload = Encoding.UTF8.GetBytes(Compile(segment.Nodes));
                var cached = await executor.ExecuteAsync(new GenerationSegmentExecutionRequest(
                    provider.ProviderStableId,
                    "synthesize-speech",
                    "account-test",
                    "voice/en-SG/A",
                    "stage5-e2e-v1",
                    $"collection-{collectionId:N}-segment-{segment.Index}",
                    payload,
                    "wav"));

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
    public async Task AmbiguousBillableSubmissionReconcilesWithoutSecondPhysicalSubmission()
    {
        var root = CreateScratchDirectory();
        try
        {
            var provider = new DeterministicFakeGenerationProvider(FakeGenerationOutcome.SubmissionUnknown);
            var executor = new GenerationSegmentExecutor(provider, new FileGenerationSegmentCache(root));
            var request = new GenerationSegmentExecutionRequest(
                provider.ProviderStableId,
                "synthesize-speech",
                "account-test",
                "voice/en-SG/A",
                "stage5-e2e-v1",
                "ambiguous-idempotency-key",
                Encoding.UTF8.GetBytes("ambiguous payload"),
                "wav");

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

    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudScribe-Stage5-E2E-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
