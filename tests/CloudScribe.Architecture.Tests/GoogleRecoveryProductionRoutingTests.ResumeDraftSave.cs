using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Fact]
    public async Task PrivateResumeDraftCancelAndExistingPartialFileCannotCreateAuthorityOrOverwrite()
    {
        using var f = new Fixture(); var r = await PrepareResumeFixtureAsync(f);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoogleTtsUsabilityHotfixBinder.SaveReservedResumeDraftAsync(f.ResumeDraftPath, r.Draft, canceled.Token));
        Assert.False(File.Exists(f.ResumeDraftPath));
        await File.WriteAllTextAsync(f.ResumeDraftPath, "synthetic incomplete private review", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => GoogleTtsUsabilityHotfixBinder.SaveReservedResumeDraftAsync(f.ResumeDraftPath, r.Draft, TestContext.Current.CancellationToken));
        Assert.Equal("synthetic incomplete private review", await File.ReadAllTextAsync(f.ResumeDraftPath, TestContext.Current.CancellationToken));
        Assert.False(r.Draft.GrantsAuthority);
    }

    [Fact]
    public async Task PrivateResumeDraftRejectsRedirectedDirectoryBeforeCreatingFile()
    {
        using var f = new Fixture(); var r = await PrepareResumeFixtureAsync(f);
        string root = Path.GetDirectoryName(f.ResumeDraftPath)!; string target = Path.Combine(root, "synthetic-target");
        string link = Path.Combine(root, "synthetic-link"); Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        { Assert.Skip("Host did not permit this temporary symbolic-link fixture; no privilege or security change attempted."); return; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => GoogleTtsUsabilityHotfixBinder.SaveReservedResumeDraftAsync(Path.Combine(link, "private.json"), r.Draft, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(target));
        Directory.Delete(link);
    }
}
