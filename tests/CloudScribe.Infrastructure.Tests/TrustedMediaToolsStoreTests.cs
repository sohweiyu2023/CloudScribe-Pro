using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class TrustedMediaToolsStoreTests
{
    [Fact]
    public async Task RememberedPairSurvivesNewStoreAndDetectsEitherChangedBinary()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-trusted-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encoder = Path.Combine(root, "ffmpeg.exe"), probe = Path.Combine(root, "ffprobe.exe");
            await File.WriteAllTextAsync(encoder, "synthetic encoder", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(probe, "synthetic probe", TestContext.Current.CancellationToken);
            var first = new TrustedMediaToolsStore(Path.Combine(root, "profile"));
            Assert.Null(await first.ReadVerifiedAsync());
            var approved = await TrustedMediaToolsStore.CaptureAsync(encoder);
            await first.RememberAsync(approved);
            Assert.Equal(approved, await new TrustedMediaToolsStore(Path.Combine(root, "profile")).ReadVerifiedAsync());
            await File.WriteAllTextAsync(probe, "changed probe", TestContext.Current.CancellationToken);
            Assert.Null(await first.ReadVerifiedAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.RememberAsync(approved));
            await File.WriteAllTextAsync(probe, "synthetic probe", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(encoder, "changed encoder", TestContext.Current.CancellationToken);
            Assert.Null(await first.ReadVerifiedAsync());
            File.Delete(encoder);
            Assert.Null(await first.ReadVerifiedAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidSavedSettingsNeverAuthorizeTools()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-trusted-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new TrustedMediaToolsStore(root);
            await File.WriteAllTextAsync(Path.Combine(root, "audio-tools.json"), "{broken", TestContext.Current.CancellationToken);
            Assert.Null(await store.ReadVerifiedAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => TrustedMediaToolsStore.CaptureAsync("ffmpeg.exe"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => TrustedMediaToolsStore.CaptureAsync(Path.Combine(root, "other.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task SelectionIsProfileScopedAndRejectsMissingProbe()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-trusted-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encoder = Path.Combine(root, "ffmpeg.exe"), probe = Path.Combine(root, "ffprobe.exe");
            await File.WriteAllTextAsync(encoder, "synthetic encoder", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(probe, "synthetic probe", TestContext.Current.CancellationToken);
            var selected = await TrustedMediaToolsStore.CaptureAsync(encoder);
            var store = new TrustedMediaToolsStore(Path.Combine(root, "first"));
            await store.RememberAsync(selected);
            Assert.Null(await new TrustedMediaToolsStore(Path.Combine(root, "second")).ReadVerifiedAsync());
            File.Delete(probe);
            Assert.Null(await store.ReadVerifiedAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => TrustedMediaToolsStore.CaptureAsync(encoder));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnrecognizedVersionDoesNotReuseChoice()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-trusted-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "audio-tools.json"), "{\"Version\":2,\"EncoderPath\":\"relative\",\"EncoderSha256\":\"fake\",\"ProbeSha256\":\"fake\"}", TestContext.Current.CancellationToken);
            Assert.Null(await new TrustedMediaToolsStore(root).ReadVerifiedAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task ApprovedSnapshotCannotBeReplacedByFreshCaptureBeforeUse()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-trusted-use-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encoder = Path.Combine(root, "ffmpeg.exe"), probe = Path.Combine(root, "ffprobe.exe");
            await File.WriteAllTextAsync(encoder, "approved encoder", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(probe, "approved probe", TestContext.Current.CancellationToken);
            var approved = await TrustedMediaToolsStore.CaptureAsync(encoder);
            await File.WriteAllTextAsync(encoder, "different encoder", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => TrustedMediaToolsStore.RequireUnchangedAsync(approved));
            await Assert.ThrowsAsync<InvalidOperationException>(() => TrustedMediaToolsStore.AcquireVerifiedLeaseAsync(approved));
            await File.WriteAllTextAsync(encoder, "approved encoder", TestContext.Current.CancellationToken);
            using (await TrustedMediaToolsStore.AcquireVerifiedLeaseAsync(approved))
            {
                if (OperatingSystem.IsWindows())
                {
                    Assert.Throws<IOException>(() => File.WriteAllText(encoder, "replace during export"));
                    Assert.Throws<IOException>(() => File.WriteAllText(probe, "replace during export"));
                }
                Assert.Equal(approved, await TrustedMediaToolsStore.CaptureAsync(encoder));
            }
            await File.WriteAllTextAsync(probe, "different probe after release", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => TrustedMediaToolsStore.RequireUnchangedAsync(approved));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InspectionExplainsWhySetupIsShownAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-trusted-reasons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encoder = Path.Combine(root, "ffmpeg.exe"), probe = Path.Combine(root, "ffprobe.exe");
            await File.WriteAllTextAsync(encoder, "synthetic encoder", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(probe, "synthetic probe", TestContext.Current.CancellationToken);
            var store = new TrustedMediaToolsStore(Path.Combine(root, "profile"));
            var none = await store.InspectAsync();
            Assert.Equal(TrustedMediaToolsState.NotChosen, none.State);
            Assert.Null(TrustedMediaToolsStore.Explain(none));

            await store.RememberAsync(await TrustedMediaToolsStore.CaptureAsync(encoder));
            var ready = await store.InspectAsync();
            Assert.Equal(TrustedMediaToolsState.Ready, ready.State);
            Assert.NotNull(ready.Selection);
            Assert.Null(TrustedMediaToolsStore.Explain(ready));

            await File.WriteAllTextAsync(encoder, "updated encoder", TestContext.Current.CancellationToken);
            var changed = await store.InspectAsync();
            Assert.Equal(TrustedMediaToolsState.Changed, changed.State);
            Assert.Null(changed.Selection);
            Assert.Equal(encoder, changed.RememberedEncoderPath);
            Assert.Contains("changed", TrustedMediaToolsStore.Explain(changed)!, StringComparison.Ordinal);

            File.Delete(probe);
            var missing = await store.InspectAsync();
            Assert.Equal(TrustedMediaToolsState.Missing, missing.State);
            Assert.Contains("no longer", TrustedMediaToolsStore.Explain(missing)!, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(root, "profile", "audio-tools.json"), "{broken", TestContext.Current.CancellationToken);
            var unreadable = await store.InspectAsync();
            Assert.Equal(TrustedMediaToolsState.SettingsUnreadable, unreadable.State);
            Assert.NotNull(TrustedMediaToolsStore.Explain(unreadable));
            Assert.Null(await store.ReadVerifiedAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
