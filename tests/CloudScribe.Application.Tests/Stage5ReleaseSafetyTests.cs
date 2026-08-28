using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5ReleaseSafetyTests
{
    [Fact]
    public void SpendAuthorizationFailsClosedOnRevisionOrProvenanceDrift()
    {
        var collectionId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var authorization = new GenerationSpendAuthorization(
            collectionId,
            new AuthorizedSpendCeiling("USD", 1000, 2),
            new Dictionary<Guid, AuthorizedSpendCeiling>
            {
                [itemId] = new("USD", 500, 2),
            },
            "pricing/v2.22/current",
            7);
        var guard = new GenerationSpendGuard();

        guard.EnsureCollectionAuthorized(authorization, new AuthorizedSpendCeiling("USD", 900, 2), 7, "pricing/v2.22/current");
        guard.EnsureItemAuthorized(authorization, itemId, new AuthorizedSpendCeiling("USD", 400, 2), 7, "pricing/v2.22/current");

        Assert.Throws<InvalidOperationException>(() =>
            guard.EnsureCollectionAuthorized(authorization, new AuthorizedSpendCeiling("USD", 900, 2), 8, "pricing/v2.22/current"));
        Assert.Throws<InvalidOperationException>(() =>
            guard.EnsureItemAuthorized(authorization, itemId, new AuthorizedSpendCeiling("USD", 400, 2), 7, "pricing/changed"));
        Assert.Throws<InvalidOperationException>(() =>
            guard.EnsureItemAuthorized(authorization, itemId, new AuthorizedSpendCeiling("USD", 501, 2), 7, "pricing/v2.22/current"));
    }

    [Fact]
    public void ExistingOutputRequiresExplicitReplacementAuthorization()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cloudscribe-stage5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "segment.wav");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
            var segment = new AudioSegmentArtifact(
                "segment-1",
                source,
                "audio/wav",
                TimeSpan.FromSeconds(3),
                new string('a', 64));
            var profile = new GenerationMasteringProfile("speech", -1m, -16m, 0, 0);
            var plan = new AudioAssemblyPlan(new[] { segment }, profile, ReleaseAudioFormat.Wav,
                TimeSpan.FromMinutes(10), root, "book");
            File.WriteAllText(plan.OutputPaths[0], "existing");
            var service = new GenerationOutputReservationService();

            Assert.Throws<IOException>(() => service.ReservePlanOutputs(plan));

            var reservations = service.ReservePlanOutputs(plan, allowExplicitReplacement: true);
            Assert.Single(reservations);
            Assert.True(reservations[0].ExistingFileWouldBeReplaced);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
