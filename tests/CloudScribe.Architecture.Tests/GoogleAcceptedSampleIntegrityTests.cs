using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleAcceptedSampleIntegrityTests
{
    [Fact]
    public async Task AcceptedBytesAreRevalidatedForCacheReplayAndExport()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-accepted-sample-test-" + Guid.NewGuid().ToString("N"));
        var service = new GoogleGenerationAcceptedMp3OutputService(root);
        byte[] bytes = [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0];
        var outcome = new GoogleGenerationQueueOutcome(
            new GoogleGenerationExecutionDecision(true, true, "accepted-test"),
            new GenerationProviderResponse(SubmissionDisposition.Accepted, "mock-sample", bytes, "audio/mpeg", null, "accepted-test"));
        GoogleGenerationAcceptedMp3Output saved = await service.PersistAcceptedAsync(outcome, TestContext.Current.CancellationToken);
        string export = Path.Combine(root, "export.mp3");
        Assert.Equal(saved.Path, await service.VerifyAcceptedAsync(saved.Path, TestContext.Current.CancellationToken));
        await service.ExportVerifiedAsync(saved.Path, export, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(export, TestContext.Current.CancellationToken));
        await File.WriteAllBytesAsync(saved.Path, [1, 2, 3], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAcceptedAsync(saved.Path, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportVerifiedAsync(saved.Path, export, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistingMediaWithoutCurrentSessionAcceptedEvidenceIsNotAdopted()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-untrusted-sample-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "unverified.mp3");
        await File.WriteAllBytesAsync(path, [0x49, 0x44, 0x33], TestContext.Current.CancellationToken);
        var service = new GoogleGenerationAcceptedMp3OutputService(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAcceptedAsync(path, TestContext.Current.CancellationToken));
    }
}
