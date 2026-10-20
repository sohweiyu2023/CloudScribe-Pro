using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class StudioPreferencesTests
{
    [Fact]
    public void FreshProfileOpensQuickStartOnceAndAllowsManualReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-help-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "studio-preferences.json");
        try
        {
            var fresh = new StudioPreferencesStore(path);
            Assert.True(fresh.IsFreshProfile);
            Assert.False(fresh.State.QuickStartSeen);
            fresh.RememberQuickStartSeen();
            var returning = new StudioPreferencesStore(path);
            Assert.False(returning.IsFreshProfile);
            Assert.True(returning.State.QuickStartSeen);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NamedSetupsSurviveReconstructionAndDeleteWithoutStoringAuthorization()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-preferences-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "preferences.json");
        try
        {
            var store = new StudioPreferencesStore(path);
            store.SaveSetup(new("AtR narration", "en-US", "en-US-Standard-C"));
            store.SaveSetup(new("Comparison", "en-GB", "en-GB-Neural2-A"));
            store.RememberDisplay(CloudScribe.App.Design.ThemePreference.CosmicPaper, true);
            Guid lastDocument = Guid.NewGuid();
            store.RememberDocument(lastDocument);
            var reopened = new StudioPreferencesStore(path);
            Assert.Equal(2, reopened.State.Setups.Length);
            Assert.Equal("en-GB-Neural2-A", reopened.State.LastUsed?.VoiceName);
            Assert.Equal(CloudScribe.App.Design.ThemePreference.CosmicPaper, reopened.State.Theme);
            Assert.True(reopened.State.ReducedMotion);
            Assert.Equal(lastDocument, reopened.State.LastDocumentId);
            Assert.True(File.Exists(path + ".previous"));
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("Credential", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Approval", json, StringComparison.OrdinalIgnoreCase);
            reopened.DeleteSetup("comparison");
            Assert.Single(new StudioPreferencesStore(path).State.Setups);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":99}")]
    [InlineData("{\"Version\":1,\"Setups\":[null]}")]
    public void UnreadablePreferencesArePreservedInsteadOfOverwritten(string original)
    {
        string path = Path.Combine(Path.GetTempPath(), "cloudscribe-preferences-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, original);
            var store = new StudioPreferencesStore(path);
            Assert.NotNull(store.Warning);
            Assert.Throws<InvalidOperationException>(() => store.SaveSetup(new("safe", "en-US", "en-US-Standard-C")));
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
