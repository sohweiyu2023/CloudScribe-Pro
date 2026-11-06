using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

/// <summary>Storage/provenance fixtures only; inert bytes and fake capture, no codec or live-provider claim.</summary>
public sealed class GoogleLongAudioRecoveryArtifactStoreTests
{
    [Fact]
    public async Task VerifiedChildReceiptSurvivesRestartAndRejectsChangedMedia()
    {
        using var f = new Fixture();
        await f.Store.RetainAsync(f.Root, f.Child, f.Artifact, TestContext.Current.CancellationToken);
        var restarted = new GoogleLongAudioRecoveryArtifactStore(f.Output, new(f.Operations));
        Assert.True(await restarted.HasVerifiedAsync(f.Child.AttemptId, f.Child.BindingSha256, TestContext.Current.CancellationToken));
        await restarted.RetainAsync(f.Root, f.Child, f.Artifact, TestContext.Current.CancellationToken);
        File.AppendAllText(f.Artifact.LocalPath, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.HasVerifiedAsync(f.Child.AttemptId, f.Child.BindingSha256, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InterruptedReceiptWriteRepairsLocallyWithoutChangingTheCompletedOperation()
    {
        using var f = new Fixture();
        string blocked = Path.Combine(f.Output, f.Child.AttemptId.ToString("N") + ".recovery-verified.json"); Directory.CreateDirectory(blocked);
        byte[] journal = File.ReadAllBytes(Path.Combine(f.Operations, f.Child.AttemptId.ToString("N") + ".json"));
        await Assert.ThrowsAnyAsync<IOException>(() => f.Store.RetainAsync(f.Root, f.Child, f.Artifact, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(f.Artifact.LocalPath)); Assert.Equal(journal, File.ReadAllBytes(Path.Combine(f.Operations, f.Child.AttemptId.ToString("N") + ".json")));
        Directory.Delete(blocked); // Empty synthetic fixture directory, not a user record.
        Assert.False(await f.Store.HasVerifiedAsync(f.Child.AttemptId, f.Child.BindingSha256, TestContext.Current.CancellationToken));
        await f.Store.RetainAsync(f.Root, f.Child, f.Artifact, TestContext.Current.CancellationToken);
        Assert.True(await f.Store.HasVerifiedAsync(f.Child.AttemptId, f.Child.BindingSha256, TestContext.Current.CancellationToken));
        Assert.Equal(journal, File.ReadAllBytes(Path.Combine(f.Operations, f.Child.AttemptId.ToString("N") + ".json")));
    }

    [Fact]
    public async Task WrongBindingAndChangedPinnedObjectAreNotAcceptedAsSavedOutput()
    {
        using var f = new Fixture();
        await f.Store.RetainAsync(f.Root, f.Child, f.Artifact, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.HasVerifiedAsync(f.Child.AttemptId, new string('b', 64), TestContext.Current.CancellationToken));
        File.WriteAllBytes(Path.Combine(f.Output, f.Child.AttemptId.ToString("N") + ".object.json"),
            JsonSerializer.SerializeToUtf8Bytes(f.Artifact.Identity with { Generation = "2" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.HasVerifiedAsync(f.Child.AttemptId, f.Child.BindingSha256, TestContext.Current.CancellationToken));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "cloudscribe-recovery-artifact-synthetic-" + Guid.NewGuid().ToString("N"));
        internal string Output { get; }
        internal string Operations { get; }
        internal GoogleLongAudioRequest Root { get; }
        internal GoogleLongAudioRequest Child { get; }
        internal GoogleLongAudioVerifiedArtifact Artifact { get; }
        internal GoogleLongAudioRecoveryArtifactStore Store { get; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Synthetic GCS MD5 transport checksum; SHA256 separately binds media.")]
        internal Fixture()
        {
            Output = Path.Combine(directory, "output"); Operations = Path.Combine(directory, "operations"); Directory.CreateDirectory(Output);
            Root = Request("First paragraph.\n\nSecond paragraph."); Child = Request("First paragraph.\n\n");
            var journal = new GoogleLongAudioFileJournal(Operations);
            Record(Operations, Root, failed: true); Record(Operations, Child, failed: false);
            string path = Path.Combine(Output, Child.AttemptId.ToString("N") + ".wav"); File.WriteAllText(path, "Inert synthetic bytes, not a decoded WAV claim.");
            byte[] bytes = File.ReadAllBytes(path);
            var identity = new GoogleLongAudioObjectIdentity(Child.BindingSha256, "synthetic-private", "output/" + Child.AttemptId.ToString("N") + ".wav", "1",
                bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)));
            Artifact = new(identity, path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
            File.WriteAllBytes(Path.Combine(Output, Child.AttemptId.ToString("N") + ".object.json"), JsonSerializer.SerializeToUtf8Bytes(identity));
            Store = new(Output, journal);
        }
        private static GoogleLongAudioRequest Request(string text)
        {
            Guid id = Guid.NewGuid(); return GoogleLongAudioRequest.Create(id, "123456", "global", "synthetic-ref", "gs://synthetic-private/output/" + id.ToString("N") + ".wav",
                text, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "LINEAR16");
        }
        private static void Record(string operations, GoogleLongAudioRequest request, bool failed)
        {
            var state = new GoogleLongAudioCheckpoint(request.AttemptId, request.BindingSha256, request.ProjectNumber, request.Location, request.CredentialReferenceId,
                request.OutputGcsUri, request.PayloadSha256, request.PayloadBytes,
                failed ? GoogleLongAudioState.Failed : GoogleLongAudioState.CompletedUnverified,
                "projects/123456/locations/global/operations/" + request.AttemptId.ToString("N"), 200, failed ? 3 : null, 1)
            {
                FailureKind = failed ? GoogleLongAudioFailureKind.SentenceLengthReported : GoogleLongAudioFailureKind.Unknown,
                Observation = new(Guid.NewGuid(), GoogleResponseReadState.Complete, GoogleLongAudioParseState.Json, GoogleErrorStatus.NotProvided,
                    200, GoogleExplanationRetentionState.SavedPrivately, true, false, false)
            };
            File.WriteAllBytes(Path.Combine(operations, request.AttemptId.ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(state));
        }
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
