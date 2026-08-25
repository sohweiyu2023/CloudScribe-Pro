using System.Globalization;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage5ReleaseCheckpointStoreTests
{
    [Fact]
    public async Task CheckpointSurvivesRestartAndCannotRegressAfterFinalization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
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
                DateTimeOffset.Parse("2026-08-23T00:00:00Z", CultureInfo.InvariantCulture));

            var first = new AtomicJsonGenerationReleaseCheckpointStore(root);
            await first.SaveAsync(pending, cancellationToken).ConfigureAwait(true);

            var restarted = new AtomicJsonGenerationReleaseCheckpointStore(root);
            var restored = await restarted.ReadAsync(collectionId, cancellationToken).ConfigureAwait(true);
            Assert.Equal(pending, restored);

            var finalized = pending with
            {
                State = GenerationReleaseCheckpointState.Finalized,
                RecordedAtUtc = pending.RecordedAtUtc.AddMinutes(1),
            };
            await restarted.SaveAsync(finalized, cancellationToken).ConfigureAwait(true);

            var regressed = pending with { RecordedAtUtc = finalized.RecordedAtUtc.AddMinutes(1) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SaveAsync(regressed, cancellationToken)).ConfigureAwait(true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OlderRevisionCannotOverwriteNewerReleaseCheckpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid();
            var store = new AtomicJsonGenerationReleaseCheckpointStore(root);
            await store.SaveAsync(new GenerationReleaseCheckpoint(
                id, 9, new string('c', 64), new string('d', 64),
                GenerationReleaseCheckpointState.PublishedPendingVerification,
                DateTimeOffset.Parse("2026-08-23T00:00:00Z", CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new GenerationReleaseCheckpoint(
                id, 8, new string('e', 64), new string('f', 64),
                GenerationReleaseCheckpointState.PublishedPendingVerification,
                DateTimeOffset.Parse("2026-08-23T00:01:00Z", CultureInfo.InvariantCulture)), cancellationToken)).ConfigureAwait(true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
