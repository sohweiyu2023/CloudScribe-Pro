using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5CollectionReleaseCoordinatorTests
{
    [Fact]
    public void ExactApprovalSpendProofAndOutputsProduceReleaseSafeDecision()
    {
        using var temp = new TempDirectory();
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var estimate = CreateEstimate(itemId);
        var approval = CreateApproval(estimate);
        var spend = CreateSpendAuthorization(estimate, itemId);
        var plan = CreatePlan(temp.Path, segmentId);
        var proof = CreateProof(segmentId, mediaValid: true);
        var coordinator = CreateCoordinator();

        var decision = coordinator.Evaluate(estimate, approval, spend, [proof], plan);

        Assert.True(decision.IsReleaseSafe);
        Assert.Equal(estimate.CollectionId, decision.CollectionId);
        Assert.Single(decision.ProofResults);
        Assert.Single(decision.OutputReservations);
        Assert.False(decision.OutputReservations[0].ExistingFileWouldBeReplaced);
    }

    [Fact]
    public void RevisionDriftBlocksReleaseBeforeOutputReservation()
    {
        using var temp = new TempDirectory();
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var estimate = CreateEstimate(itemId);
        var approval = CreateApproval(estimate) with { RequestRevision = estimate.RequestRevision + 1 };
        var spend = CreateSpendAuthorization(estimate, itemId);
        var plan = CreatePlan(temp.Path, segmentId);
        var coordinator = CreateCoordinator();

        var error = Assert.Throws<InvalidOperationException>(() =>
            coordinator.Evaluate(estimate, approval, spend, [CreateProof(segmentId, true)], plan));

        Assert.Contains("exact current approval", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(plan.OutputPaths[0]));
    }

    [Fact]
    public void QuarantinedProofBlocksRelease()
    {
        using var temp = new TempDirectory();
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var estimate = CreateEstimate(itemId);
        var coordinator = CreateCoordinator();

        var error = Assert.Throws<InvalidOperationException>(() =>
            coordinator.Evaluate(
                estimate,
                CreateApproval(estimate),
                CreateSpendAuthorization(estimate, itemId),
                [CreateProof(segmentId, mediaValid: false)],
                CreatePlan(temp.Path, segmentId)));

        Assert.Contains("quarantined", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProofAndAssemblyIdentityMismatchBlocksRelease()
    {
        using var temp = new TempDirectory();
        var itemId = Guid.NewGuid();
        var estimate = CreateEstimate(itemId);
        var coordinator = CreateCoordinator();

        var error = Assert.Throws<InvalidOperationException>(() =>
            coordinator.Evaluate(
                estimate,
                CreateApproval(estimate),
                CreateSpendAuthorization(estimate, itemId),
                [CreateProof(Guid.NewGuid(), mediaValid: true)],
                CreatePlan(temp.Path, Guid.NewGuid())));

        Assert.Contains("exact same segment identities", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingOutputRequiresExplicitReplacementAuthorization()
    {
        using var temp = new TempDirectory();
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var estimate = CreateEstimate(itemId);
        var plan = CreatePlan(temp.Path, segmentId);
        File.WriteAllText(plan.OutputPaths[0], "existing");
        var coordinator = CreateCoordinator();

        Assert.Throws<IOException>(() => coordinator.Evaluate(
            estimate,
            CreateApproval(estimate),
            CreateSpendAuthorization(estimate, itemId),
            [CreateProof(segmentId, mediaValid: true)],
            plan));

        var allowed = coordinator.Evaluate(
            estimate,
            CreateApproval(estimate),
            CreateSpendAuthorization(estimate, itemId),
            [CreateProof(segmentId, mediaValid: true)],
            plan,
            allowExplicitOutputReplacement: true);

        Assert.True(allowed.OutputReservations[0].ExistingFileWouldBeReplaced);
    }

    private static GenerationCollectionReleaseCoordinator CreateCoordinator() => new(
        new GenerationSpendGuard(),
        new GenerationProofPass(),
        new GenerationOutputReservationService(),
        TimeProvider.System);

    private static GenerationCollectionEstimate CreateEstimate(Guid itemId)
    {
        var item = new GenerationItemEstimate(itemId, 0, "USD", 120, 2);
        return new GenerationCollectionEstimate(
            Guid.NewGuid(),
            7,
            DateTimeOffset.UtcNow,
            "USD",
            120,
            2,
            "pricing:v2.22",
            [item]);
    }

    private static GenerationApproval CreateApproval(GenerationCollectionEstimate estimate) => new(
        estimate.CollectionId,
        estimate.RequestRevision,
        estimate.PricingProvenanceId,
        estimate.Currency,
        estimate.ScaledTotal,
        estimate.Scale,
        DateTimeOffset.UtcNow);

    private static GenerationSpendAuthorization CreateSpendAuthorization(GenerationCollectionEstimate estimate, Guid itemId) => new(
        estimate.CollectionId,
        new AuthorizedSpendCeiling(estimate.Currency, estimate.ScaledTotal, estimate.Scale),
        new Dictionary<Guid, AuthorizedSpendCeiling>
        {
            [itemId] = new(estimate.Currency, estimate.Items[0].ScaledAmount, estimate.Scale),
        },
        estimate.PricingProvenanceId,
        estimate.RequestRevision);

    private static GenerationProofInput CreateProof(Guid segmentId, bool mediaValid) => new(
        segmentId,
        mediaValid,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        true,
        [],
        "provider:test");

    private static AudioAssemblyPlan CreatePlan(string outputDirectory, Guid segmentId)
    {
        var sourcePath = Path.Combine(outputDirectory, $"{segmentId:N}.wav");
        File.WriteAllBytes(sourcePath, [0x52, 0x49, 0x46, 0x46]);
        var artifact = new AudioSegmentArtifact(
            segmentId.ToString("D"),
            sourcePath,
            "audio/wav",
            TimeSpan.FromSeconds(10),
            new string('a', 64));
        var mastering = new GenerationMasteringProfile("spoken-default", -1m, -16m, 0, 0);
        return new AudioAssemblyPlan(
            [artifact],
            mastering,
            ReleaseAudioFormat.Wav,
            TimeSpan.FromMinutes(30),
            outputDirectory,
            "release");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cloudscribe-stage5-{Guid.NewGuid():N}");
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
