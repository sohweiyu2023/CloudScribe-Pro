using CloudScribe.Domain.Security;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8ArchiveExtractionPolicyTests
{
    [Fact]
    public void ValidEntriesResolveInsideRoot()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-root"));
        var policy = new ArchiveExtractionPolicy(1024, 4096, 10);

        var resolved = policy.ValidateAndResolve(root,
        [
            new ArchiveEntryDescriptor("a/file.txt", 10, false),
            new ArchiveEntryDescriptor("b/file.txt", 20, false),
        ]);

        Assert.Equal(2, resolved.Count);
        Assert.All(resolved, path => Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    public void ZipSlipIsRejected(string entry)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-root"));
        var policy = new ArchiveExtractionPolicy(1024, 4096, 10);

        Assert.Throws<InvalidOperationException>(() =>
            policy.ValidateAndResolve(root, [new ArchiveEntryDescriptor(entry, 1, false)]));
    }

    [Fact]
    public void SymlinkIsRejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-root"));
        var policy = new ArchiveExtractionPolicy(1024, 4096, 10);

        Assert.Throws<InvalidOperationException>(() =>
            policy.ValidateAndResolve(root, [new ArchiveEntryDescriptor("link", 0, true)]));
    }

    [Fact]
    public void CaseInsensitiveOutputCollisionIsRejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-root"));
        var policy = new ArchiveExtractionPolicy(1024, 4096, 10);

        Assert.Throws<InvalidOperationException>(() =>
            policy.ValidateAndResolve(root,
            [
                new ArchiveEntryDescriptor("A.txt", 1, false),
                new ArchiveEntryDescriptor("a.txt", 1, false),
            ]));
    }

    [Fact]
    public void TotalExpansionLimitIsRejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-root"));
        var policy = new ArchiveExtractionPolicy(10, 15, 10);

        Assert.Throws<InvalidOperationException>(() =>
            policy.ValidateAndResolve(root,
            [
                new ArchiveEntryDescriptor("a", 10, false),
                new ArchiveEntryDescriptor("b", 6, false),
            ]));
    }
}
