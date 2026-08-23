using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage5ReleaseCheckpointStoreTests
{
    [Fact]
    public async Task CheckpointSurvivesRestartAndCannotRegressAfterFinalization()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var collectionId = Guid.NewGuid();
            var pending = new GenerationReleaseCheckpoint(
                collectionId,
                4,
                new string('a', 64),
                new string('b', 64),
                GenerationReleaseCheckpointState.PublishedPendingVerification,
                DateTimeOffset.Parse("2026-08-23T00:00:00Z"));

            var first = new AtomicJsonGenerationReleaseCheckpointStore(root);
            await first.SaveAsync(pending);

            var restarted = new AtomicJsonGenerationReleaseCheckpointStore(root);
            var restored = await restarted.ReadAsync(collectionId);
            Assert.Equal(pending, restored);

            var finalized = pending with
            {
                State = GenerationReleaseCheckpointState.Finalized,
                RecordedAtUtc = pending.RecordedAtUtc.AddMinutes(1),
            };
            await restarted.SaveAsync(finalized);

            var regressed = pending with { RecordedAtUtc = finalized.RecordedAtUtc.AddMinutes(1) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SaveAsync(regressed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OlderRevisionCannotOverwriteNewerReleaseCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid();
            var store = new AtomicJsonGenerationReleaseCheckpointStore(root);
            await store.SaveAsync(new GenerationReleaseCheckpoint(
                id, 9, new string('c', 64), new string('d', 64),
                GenerationReleaseCheckpointState.PublishedPendingVerification,
                DateTimeOffset.Parse("2026-08-23T00:00:00Z")));

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new GenerationReleaseCheckpoint(
                id, 8, new string('e', 64), new string('f', 64),
                GenerationReleaseCheckpointState.PublishedPendingVerification,
                DateTimeOffset.Parse("2026-08-23T00:01:00Z"))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
