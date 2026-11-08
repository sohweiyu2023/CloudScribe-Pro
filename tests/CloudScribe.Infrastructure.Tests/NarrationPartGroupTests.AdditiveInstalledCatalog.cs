using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static readonly string[] InstalledComparisonVoices = ["en-US-Neural2-D", "en-US-Chirp3-HD-Aoede"];

    [Fact]
    public async Task AdditiveInstalledTypedAdmissionLoadsBothVoicesAndDoesNotWriteEvidence()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        var store = p.Store(); var initial = await store.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken);
        Assert.Empty(initial.Families); Assert.Equal(ChirpPolicyValidation.Hash(p.Legacy), ChirpPolicyValidation.Hash(initial.LegacyPolicy));
        Assert.False(Directory.Exists(p.Root));
        var family = p.Append(Assert.Single(p.Evidence.Store.Catalog.Families));
        var before = InstalledTreeHashes(p.Install);
        var loaded = await store.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ChirpPolicyValidation.Hash(family), ChirpPolicyValidation.Hash(Assert.Single(loaded.Families)));
        Assert.Equal(InstalledComparisonVoices, loaded.Families[0].Policy.Articles.Select(x => x.Proposal.VoiceName), StringComparer.Ordinal);
        var review = await p.Service(store).PrepareAsync(FullDocumentIntent(p.Evidence.First, 1), TestContext.Current.CancellationToken);
        Assert.Equal(2, review.MaximumMinorUnits);
        Assert.Equal(family.Policy.Articles[0].Proposal.PlanSha256, review.PlanSha256);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(before, InstalledTreeHashes(p.Install));
    }

    [Fact]
    public async Task AdditiveInstalledBuildOnlyRebindPreservesImmutableGrantAndOriginalPolicyIdentity()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        var family = p.Append(Assert.Single(p.Evidence.Store.Catalog.Families));
        var store = p.Store(); _ = await store.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken);
        string originalPolicy = ChirpPolicyValidation.Hash(family.Policy), originalLegacy = ChirpPolicyValidation.Hash(p.Legacy);
        string grantPath = p.PinPath("grants", Assert.Single(p.Head!.Families).GrantSha256); byte[] grant = File.ReadAllBytes(grantPath);
        p.Rebind();
        foreach (var reader in new[] { store, p.Store() })
        {
            var loaded = await reader.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken);
            Assert.Equal(originalPolicy, ChirpPolicyValidation.Hash(Assert.Single(loaded.Families).Policy));
            Assert.Equal(ChirpPolicyValidation.Hash(p.Legacy), ChirpPolicyValidation.Hash(loaded.LegacyPolicy));
            Assert.NotEqual(originalLegacy, ChirpPolicyValidation.Hash(loaded.LegacyPolicy), StringComparer.Ordinal);
            _ = await p.Service(reader).PrepareAsync(FullDocumentIntent(p.Evidence.First, 1), TestContext.Current.CancellationToken);
        }
        Assert.Equal(grant, File.ReadAllBytes(grantPath));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task AdditiveInstalledExpiredSiblingIsRetainedForAccountingButCannotReserveAgain()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        var first = p.Append(Assert.Single(p.Evidence.Store.Catalog.Families));
        var sibling = await InstalledSiblingAsync(f, first);
        sibling = p.Append(sibling, "synthetic expired sibling approval", x => x with { Policy = x.Policy with { ExpiresAtUtc = p.Evidence.Legacy.Clock.Now.AddSeconds(-1) } });
        var reader = p.Store(); var loaded = await reader.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, loaded.Families.Count);
        Assert.Equal(ChirpPolicyValidation.Hash(sibling), ChirpPolicyValidation.Hash(loaded.Families[1]));
        await Assert.ThrowsAsync<InvalidDataException>(() => p.Service(reader).PrepareWholeArticleAsync(sibling.Policy.Articles[0].Proposal.DocumentId, TestContext.Current.CancellationToken));
        _ = await p.Service(reader).PrepareAsync(FullDocumentIntent(p.Evidence.First, 1), TestContext.Current.CancellationToken);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task AdditiveInstalledExpiredLegacyDoesNotRevokeCurrentIndependentFamilyOrRenewLegacyAuthority()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        p.Legacy = p.Legacy with { ExpiresAtUtc = p.Evidence.Legacy.Clock.Now.AddSeconds(-1) }; p.WriteLegacy();
        p.Append(Assert.Single(p.Evidence.Store.Catalog.Families));
        var reader = p.Store(); var loaded = await reader.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ChirpPolicyValidation.Hash(p.Legacy), ChirpPolicyValidation.Hash(loaded.LegacyPolicy));
        Assert.Single(loaded.Families);
        Assert.Null(await new InstalledChirpArticleAdmissionStore(p.App, f.Paths, p.Evidence.Legacy.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken));
        _ = await p.Service(reader).PrepareAsync(FullDocumentIntent(p.Evidence.First, 1), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => p.Service(reader).PrepareAsync(ChirpIntent(p.Evidence.Legacy), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdditiveInstalledMissingOrRolledBackHeadCannotHidePublishedSiblingAfterRestart(bool remove)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        var first = p.Append(Assert.Single(p.Evidence.Store.Catalog.Families)); byte[] previous = File.ReadAllBytes(p.RegistryPath);
        p.Append(await InstalledSiblingAsync(f, first), "synthetic second approval");
        var reader = p.Store(); Assert.Equal(2, (await reader.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken)).Families.Count);
        if (remove) File.Delete(p.RegistryPath); else File.WriteAllBytes(p.RegistryPath, previous);
        await RejectInstalledCatalogAsync(reader); await RejectInstalledCatalogAsync(p.Store());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdditiveInstalledReplayedSourceApprovalCannotCreateIndependentAllowance(bool replayLegacy)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        var original = Assert.Single(p.Evidence.Store.Catalog.Families);
        if (replayLegacy)
        {
            p.Legacy = p.Legacy with { SourceApprovalSha256 = Hash("synthetic replayed approval") }; p.WriteLegacy();
            p.Append(original, "synthetic replayed approval");
        }
        else
        {
            var first = p.Append(original, "synthetic replayed approval");
            p.Append(await InstalledSiblingAsync(f, first), "synthetic replayed approval");
        }
        await RejectInstalledCatalogAsync(p.Store());
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    private static string[] InstalledTreeHashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + InstalledBytesHash(File.ReadAllBytes(path))).ToArray();

    private static async Task RejectInstalledCatalogAsync(InstalledChirpArticleAdmissionCatalogStore store)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken)).ConfigureAwait(false);
        Assert.Equal("The independent article admission catalog is missing, changed or incomplete.", error.Message);
    }
}
