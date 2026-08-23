using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7ReleaseProviderSetManifestTests
{
    [Fact]
    public void Manifest_IsDeterministic_AndRejectsUnadmittedProviderOrOperation()
    {
        var descriptor = new ReleaseProviderDescriptor(
            "provider-a",
            "Provider A",
            new string('b', 64),
            new HashSet<string>(StringComparer.Ordinal) { "tts.generate" });

        var first = new ReleaseProviderSetManifest(
            new string('a', 64),
            "controls/release-providers.json",
            new[] { descriptor });
        var second = new ReleaseProviderSetManifest(
            new string('a', 64),
            "controls/release-providers.json",
            new[] { descriptor });

        Assert.Equal(first.ManifestSha256, second.ManifestSha256);
        Assert.Equal("provider-a", first.RequireProvider("provider-a").ProviderStableId);
        Assert.Equal("provider-a", first.RequireProviderOperation("provider-a", "tts.generate").ProviderStableId);
        Assert.Throws<InvalidOperationException>(() => first.RequireProvider("provider-b"));
        Assert.Throws<InvalidOperationException>(() => first.RequireProviderOperation("provider-a", "voices.clone"));
    }

    [Fact]
    public void Manifest_RejectsDuplicateStableProviderIdentity()
    {
        var descriptor = new ReleaseProviderDescriptor(
            "provider-a",
            "Provider A",
            new string('b', 64),
            new HashSet<string> { "tts.generate" });

        Assert.Throws<ArgumentException>(() => new ReleaseProviderSetManifest(
            new string('a', 64),
            "controls/release-providers.json",
            new[] { descriptor, descriptor }));
    }
}
