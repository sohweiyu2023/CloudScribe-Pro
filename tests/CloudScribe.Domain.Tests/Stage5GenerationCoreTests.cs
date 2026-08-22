using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5GenerationCoreTests
{
    [Fact]
    public void SpeechPlanPreservesProviderNeutralSemantics()
    {
        var plan = new SpeechPlan(
            "en-SG",
            [
                new SpeechChapter("chapter-1", "Opening"),
                new SpeechSpeakerChange("narrator"),
                new SpeechVoice("narrator", "voice/en-SG/A"),
                new SpeechProsody(1.05m, 0m, -1m),
                new SpeechText("Hello world."),
                new SpeechPronunciation("CloudScribe", "ipa", "klaʊd skraɪb"),
                new SpeechPause(TimeSpan.FromMilliseconds(250)),
                new SpeechEmphasis(SpeechEmphasisLevel.Moderate),
                new SpeechMark("paragraph-1"),
                new SpeechTimestampRequest("paragraph-1"),
            ],
            "document/revision/7");

        Assert.Equal("en-SG", plan.LanguageTag);
        Assert.Equal(10, plan.Nodes.Count);
        Assert.Equal("document/revision/7", plan.ProvenanceId);
    }

    [Fact]
    public void SegmenterDoesNotSplitExtendedGraphemeCluster()
    {
        var plan = new SpeechPlan(
            "und",
            [new SpeechText("A👨‍👩‍👧‍👦B🇸🇬C")],
            "unicode-test");

        var segments = SpeechPlanSegmenter.Segment(
            plan,
            new SpeechSegmentationLimits(2, 100),
            static nodes => nodes.OfType<SpeechText>().Sum(static text => text.Text.Length));

        Assert.Equal(3, segments.Count);
        Assert.All(segments, static segment => Assert.InRange(segment.TextElementCount, 1, 2));
        Assert.Equal(
            "A👨‍👩‍👧‍👦B🇸🇬C",
            string.Concat(segments.SelectMany(static segment => segment.Nodes).OfType<SpeechText>().Select(static text => text.Text)));
    }

    [Fact]
    public void SegmenterTreatsPronunciationAsIndivisibleProtectedNode()
    {
        var plan = new SpeechPlan(
            "en",
            [new SpeechPronunciation("protected", "ipa", "pɹəˈtɛktɪd")],
            "pronunciation-test");

        Assert.Throws<InvalidOperationException>(() => SpeechPlanSegmenter.Segment(
            plan,
            new SpeechSegmentationLimits(4, 100),
            static nodes => nodes.Count));
    }

    [Fact]
    public void SegmenterEnforcesPostCompilationPayloadLimit()
    {
        var plan = new SpeechPlan(
            "en",
            [new SpeechText("one two three")],
            "compile-limit-test");

        Assert.Throws<InvalidOperationException>(() => SpeechPlanSegmenter.Segment(
            plan,
            new SpeechSegmentationLimits(100, 3),
            static nodes => nodes.OfType<SpeechText>().Sum(static text => text.Text.Length)));
    }

    [Fact]
    public void SnapshotRequiresOrderedContiguousSegments()
    {
        var plan = new SpeechPlan("en", [new SpeechText("hello")], "source/1");
        var segment = new GenerationSegmentSnapshot(1, "sha256:abc", 5, 5, "hello");

        Assert.Throws<ArgumentException>(() => new GenerationJobSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            DateTimeOffset.UtcNow,
            plan,
            "account-1",
            "synthesize-speech",
            "pricing-1",
            "runtime-1",
            [segment]));
    }

    [Fact]
    public void ApprovalIsInvalidatedByRevisionOrPricingChange()
    {
        var collectionId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var estimate = new GenerationCollectionEstimate(
            collectionId,
            4,
            DateTimeOffset.UtcNow,
            "USD",
            125,
            2,
            "catalog-A",
            [new GenerationItemEstimate(itemId, 0, "USD", 125, 2)]);
        var approval = new GenerationApproval(
            collectionId,
            4,
            "catalog-A",
            "USD",
            150,
            2,
            DateTimeOffset.UtcNow);

        Assert.True(approval.Authorizes(estimate));

        var changedRevision = new GenerationCollectionEstimate(
            collectionId,
            5,
            DateTimeOffset.UtcNow,
            "USD",
            125,
            2,
            "catalog-A",
            [new GenerationItemEstimate(itemId, 0, "USD", 125, 2)]);
        var changedCatalog = new GenerationCollectionEstimate(
            collectionId,
            4,
            DateTimeOffset.UtcNow,
            "USD",
            125,
            2,
            "catalog-B",
            [new GenerationItemEstimate(itemId, 0, "USD", 125, 2)]);

        Assert.False(approval.Authorizes(changedRevision));
        Assert.False(approval.Authorizes(changedCatalog));
    }

    [Fact]
    public void CollectionEstimateRejectsMismatchedAggregate()
    {
        Assert.Throws<ArgumentException>(() => new GenerationCollectionEstimate(
            Guid.NewGuid(),
            0,
            DateTimeOffset.UtcNow,
            "USD",
            999,
            2,
            "pricing-A",
            [new GenerationItemEstimate(Guid.NewGuid(), 0, "USD", 100, 2)]));
    }

    [Fact]
    public void SubmissionUnknownCannotAutomaticallyReturnToSubmitting()
    {
        Assert.True(GenerationJobStateMachine.RequiresReconciliationBeforeAutomaticRetry(GenerationJobState.SubmissionUnknown));
        Assert.False(GenerationJobStateMachine.CanTransition(GenerationJobState.SubmissionUnknown, GenerationJobState.Submitting));
        Assert.Throws<InvalidOperationException>(() =>
            GenerationJobStateMachine.EnsureTransition(GenerationJobState.SubmissionUnknown, GenerationJobState.Submitting));
    }

    [Fact]
    public void ImpossibleTerminalTransitionsAreRejected()
    {
        Assert.True(GenerationJobStateMachine.IsTerminal(GenerationJobState.Completed));
        Assert.False(GenerationJobStateMachine.CanTransition(GenerationJobState.Completed, GenerationJobState.Queued));
        Assert.False(GenerationJobStateMachine.CanTransition(GenerationJobState.CancelledReconciled, GenerationJobState.Running));
    }
}
