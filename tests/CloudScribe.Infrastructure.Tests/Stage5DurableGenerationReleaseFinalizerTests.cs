using System.Security.Cryptography;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage5DurableGenerationReleaseFinalizerTests
{
    [Fact]
    public async Task RecoverAsync_FinalizesPendingCheckpointAfterDiskVerification()
    {
        var root = NewTempDirectory();
        try
        {
            var output = Path.Combine(root, "release.wav");
            await File.WriteAllBytesAsync(output, new byte[] { 1, 2, 3, 4 });
            var receipt = CreateReceipt(output);
            var store = new AtomicJsonGenerationReleaseCheckpointStore(Path.Combine(root, "checkpoints"));
            await store.SaveAsync(GenerationReleaseCheckpoint.FromReceipt(
                receipt,
                GenerationReleaseCheckpointState.PublishedPendingVerification,
                DateTimeOffset.UtcNow.AddSeconds(-1)));
            var service = new DurableGenerationReleaseFinalizer(
                new GenerationReleasePublisher(),
                new GenerationReleaseVerifier(),
                store);

            var result = await service.RecoverAsync(receipt);

            Assert.True(result.IsFinalized);
            var persisted = await store.ReadAsync(receipt.CollectionId);
            Assert.NotNull(persisted);
            Assert.Equal(GenerationReleaseCheckpointState.Finalized, persisted!.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoverAsync_TamperedOutputRemainsPendingAndFailsClosed()
    {
        var root = NewTempDirectory();
        try
        {
            var output = Path.Combine(root, "release.wav");
            await File.WriteAllBytesAsync(output, new byte[] { 5, 6, 7, 8 });
            var receipt = CreateReceipt(output);
            var store = new AtomicJsonGenerationReleaseCheckpointStore(Path.Combine(root, "checkpoints"));
            await store.SaveAsync(GenerationReleaseCheckpoint.FromReceipt(
                receipt,
                GenerationReleaseCheckpointState.PublishedPendingVerification,
                DateTimeOffset.UtcNow.AddSeconds(-1)));
            await File.WriteAllBytesAsync(output, new byte[] { 9, 9, 9, 9 });
            var service = new DurableGenerationReleaseFinalizer(
                new GenerationReleasePublisher(),
                new GenerationReleaseVerifier(),
                store);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.RecoverAsync(receipt));

            var persisted = await store.ReadAsync(receipt.CollectionId);
            Assert.NotNull(persisted);
            Assert.Equal(GenerationReleaseCheckpointState.PublishedPendingVerification, persisted!.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GenerationReleaseReceipt CreateReceipt(string outputPath)
    {
        var bytes = File.ReadAllBytes(outputPath);
        var outputSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return GenerationReleaseReceipt.Create(
            Guid.NewGuid(),
            1,
            "pricing-prov",
            "approval-1",
            outputPath,
            outputSha,
            new[]
            {
                new GenerationReleaseSegmentReceipt(
                    Guid.NewGuid(),
                    "cache-key",
                    new string('a', 64),
                    "proof-prov",
                    true),
            });
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
