using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleAcceptedOutputDirectoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedOutputCanBeVerifiedAndExportedWithEitherDirectorySpelling(bool trailingSeparator)
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-output-directory-test-" + Guid.NewGuid().ToString("N"));
        string outputDirectory = Path.Combine(root, "generated");
        var service = new GoogleGenerationAcceptedMp3OutputService(
            trailingSeparator ? outputDirectory + Path.DirectorySeparatorChar : outputDirectory);
        byte[] bytes = [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0];
        var outcome = new GoogleGenerationQueueOutcome(
            new GoogleGenerationExecutionDecision(true, true, "accepted-local-fixture"),
            new GenerationProviderResponse(SubmissionDisposition.Accepted, "mock-output", bytes, "audio/mpeg", null, "accepted-local-fixture"));
        try
        {
            GoogleGenerationAcceptedMp3Output saved = await service.PersistAcceptedAsync(outcome, TestContext.Current.CancellationToken);
            string export = Path.Combine(root, "export.mp3");

            Assert.Equal(saved.Path, await service.VerifyAcceptedAsync(saved.Path, TestContext.Current.CancellationToken));
            Assert.Equal(export, await service.ExportVerifiedAsync(saved.Path, export, TestContext.Current.CancellationToken));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(export, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
