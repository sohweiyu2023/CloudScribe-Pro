using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class CompletionGateActivationTests
{
    [Theory]
    [InlineData("missing-required")]
    [InlineData("missing-pin")]
    [InlineData("changed-gate")]
    [InlineData("changed-build")]
    [InlineData("changed-profile")]
    [InlineData("legacy-protocol")]
    public void PartialOrChangedActivationNeverMeansUngated(string change)
    {
        using var fixture = new ActivationFixture();
        switch (change)
        {
            case "missing-required": File.Delete(fixture.Required); break;
            case "missing-pin": fixture.WriteMarker(false, 1); break;
            case "changed-gate": File.AppendAllText(fixture.GatePath, " "); break;
            case "changed-build": File.WriteAllText(Path.Combine(fixture.App, "CloudScribe.dll"), "changed inert test bytes"); break;
            case "changed-profile": File.WriteAllText(fixture.Required, "{\"GateSha256\":\"bad\",\"ScopeSha256\":\"bad\"}"); break;
            case "legacy-protocol": fixture.WriteMarker(true, 0); break;
        }
        var error = Assert.Throws<TargetInvocationException>(fixture.ReadActualInstalledParser);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void ExactPersistentActivationIsReadFromBothIndependentLocations()
    {
        using var fixture = new ActivationFixture();
        var parsed = Assert.IsType<CompletionGateConfiguration>(fixture.ReadActualInstalledParser());
        Assert.Equal(fixture.Pin, parsed.Pin);
        Assert.Equal(fixture.ProfileHash, parsed.ProfileRootSha256);
    }

    [Fact]
    public void NeverActivatedProfileRemainsAnOrdinaryApplication()
    {
        using var fixture = new ActivationFixture();
        File.Delete(fixture.Required);
        fixture.WriteMarker(false, 1);
        Assert.Null(fixture.ReadActualInstalledParser());
    }

    private sealed class ActivationFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudscribe-completion-parser-" + Guid.NewGuid().ToString("N"));
        private readonly string _profile;
        private readonly string _install;
        public string App { get; }
        public string Required { get; }
        public string GatePath { get; }
        public string Pin { get; }
        public string ProfileHash { get; }

        public ActivationFixture()
        {
            _install = Path.Combine(_root, "install"); _profile = Path.Combine(_root, "profile");
            App = Path.Combine(_install, "app"); Directory.CreateDirectory(App); Directory.CreateDirectory(_profile);
            File.WriteAllText(Path.Combine(App, "CloudScribe.dll"), "inert synthetic app");
            File.WriteAllText(Path.Combine(App, "CloudScribe.Infrastructure.dll"), "inert synthetic infrastructure");
            ProfileHash = InstalledRecoveryAdmissionStore.ProfileHash(_profile);
            var configuration = new CompletionGateConfiguration(1, "synthetic", "", "synthetic", "", "", "synthetic", new string('a', 64),
                App, App, FileHash(Path.Combine(App, "CloudScribe.dll")), FileHash(Path.Combine(App, "CloudScribe.Infrastructure.dll")),
                ProfileHash, "synthetic-account", "synthetic-project", "synthetic-reference", "https://texttospeech.googleapis.com/v1/voices",
                "https://texttospeech.googleapis.com/v1/text:synthesize", "synthetic", "", "cmn-CN-Chirp3-HD-Alnilam", "cmn-CN");
            GatePath = Path.Combine(_install, "completion-workflow-gate.json");
            File.WriteAllText(GatePath, JsonSerializer.Serialize(configuration)); Pin = FileHash(GatePath);
            Required = Path.Combine(_profile, ".completion-workflow-required.json");
            File.WriteAllText(Required, JsonSerializer.Serialize(new { GateSha256 = Pin, configuration.ScopeSha256 }));
            WriteMarker(true, 1);
        }

        public void WriteMarker(bool pin, int protocol)
        {
            var marker = new Dictionary<string, object>(StringComparer.Ordinal)
            { ["Product"] = "CloudScribe Pro", ["CompletionGateProtocolVersion"] = protocol };
            if (pin) marker["CompletionWorkflowGateSha256"] = Pin;
            File.WriteAllText(Path.Combine(_install, "cloudscribe-install.json"), JsonSerializer.Serialize(marker));
        }

        public object? ReadActualInstalledParser() => typeof(CompletionWorkflowGate)
            .GetMethod("LoadInstalled", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [_profile, App]);

        private static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        public void Dispose()
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(_root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Synthetic cleanup escaped its explicit temporary parent.");
            Directory.Delete(_root, recursive: true);
        }
    }
}
