using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task AssertUnrelatedRecoveryMediaIsNotReadAsync(GoogleLongAudioRecoveryRunner runner,
        GoogleLongAudioRequest root, string directory, Func<int> calls)
    {
        Guid id = Guid.NewGuid();
        var unrelated = GoogleLongAudioRequest.Create(id, root.ProjectNumber, root.Location, root.CredentialReferenceId,
            "gs://synthetic-private/output/" + id.ToString("N") + ".wav", "Unrelated synthetic article.", root.LanguageCode, root.VoiceName, "LINEAR16");
        var completed = AdaptiveRejected(unrelated) with { State = GoogleLongAudioState.CompletedUnverified, RpcCode = null };
        string journal = Path.Combine(directory, "operations", id.ToString("N") + ".json");
        string receipt = Path.Combine(directory, "output", id.ToString("N") + ".recovery-verified.json");
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(completed);
        File.WriteAllText(receipt, "{}"); // Unrelated corrupt local artifact must not be opened by the global metadata scan.
        try
        {
            int before = calls();
            File.WriteAllBytes(journal, JsonSerializer.SerializeToUtf8Bytes(completed with { State = GoogleLongAudioState.PostClaimed }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken)).ConfigureAwait(false);
            Assert.Equal(before, calls());
            File.WriteAllText(journal, "{ malformed synthetic journal");
            await Assert.ThrowsAnyAsync<JsonException>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken)).ConfigureAwait(false);
            Assert.Equal(before, calls());
            File.WriteAllBytes(journal, original);
            var resumed = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.True(resumed.CoverageComplete);
            Assert.Equal("{}", File.ReadAllText(receipt));
            Assert.Equal(original, File.ReadAllBytes(journal));
        }
        finally { File.Delete(journal); File.Delete(receipt); }
    }
}
