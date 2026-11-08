using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("infrastructure")]
    [InlineData("profile")]
    [InlineData("marker")]
    [InlineData("pending-marker")]
    [InlineData("legacy")]
    [InlineData("missing-grant")]
    [InlineData("tampered-grant")]
    [InlineData("missing-receipt")]
    [InlineData("missing-plan")]
    [InlineData("missing-proof")]
    [InlineData("missing-source")]
    [InlineData("tampered-source")]
    [InlineData("missing-snapshot")]
    [InlineData("missing-history")]
    [InlineData("tampered-history")]
    [InlineData("duplicate-sibling")]
    [InlineData("dropped-sibling")]
    [InlineData("null-registry-families")]
    [InlineData("null-registry-entry")]
    [InlineData("unknown-registry-field")]
    [InlineData("rolled-back-transaction")]
    [InlineData("missing-transaction")]
    public async Task AdditiveInstalledEveryCurrentAndRetainedPinFailsClosed(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        var first = p.Append(Assert.Single(p.Evidence.Store.Catalog.Families));
        string previous = p.HeadSha!;
        p.Append(await InstalledSiblingAsync(f, first), "synthetic second approval");
        var reader = p.Store(); Assert.Equal(2, (await reader.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken)).Families.Count);
        MutateInstalledCatalog(p, previous, fault);
        if (fault is "profile")
        {
            using var other = new Fixture();
            await RejectInstalledCatalogAsync(new(p.App, other.Paths, p.Evidence.Legacy.Clock));
        }
        else { await RejectInstalledCatalogAsync(reader); await RejectInstalledCatalogAsync(p.Store()); }
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Theory]
    [InlineData("duplicate-field")]
    [InlineData("unknown-field")]
    [InlineData("null-policy")]
    [InlineData("null-requests")]
    [InlineData("null-request")]
    [InlineData("null-article")]
    [InlineData("null-family")]
    public async Task AdditiveInstalledMalformedHashConsistentGrantNeverSuppliesAuthority(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        p.Append(Assert.Single(p.Evidence.Store.Catalog.Families), rawFault: fault);
        await RejectInstalledCatalogAsync(p.Store());
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Theory]
    [InlineData("Passed")]
    [InlineData("ProviderCalls")]
    [InlineData("LiveWrites")]
    [InlineData("FamilyPolicySha256")]
    [InlineData("LegacyLedgerSha256")]
    [InlineData("AllInCeilingMinorUnits")]
    public async Task AdditiveInstalledHashConsistentNativeProofMustMatchTypedFamilyAndZeroProviderReview(string field)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        p.Append(Assert.Single(p.Evidence.Store.Catalog.Families), proofChange: proof => proof[field] = field switch
        {
            "Passed" => false,
            "ProviderCalls" or "LiveWrites" => 1,
            "AllInCeilingMinorUnits" => 1000000,
            _ => Hash("different synthetic review")
        });
        await RejectInstalledCatalogAsync(p.Store());
    }

    [Theory]
    [InlineData("PreviousLegacyPolicyIdentitySha256")]
    [InlineData("NewLegacyPolicyIdentitySha256")]
    [InlineData("NewParentSha256")]
    [InlineData("NewScopeSha256")]
    [InlineData("AdditiveRegistrySha256")]
    public async Task AdditiveInstalledHashConsistentRebindCannotChangeReviewedTransitionPins(string field)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var p = await InstalledAdditiveAsync(f);
        p.Append(Assert.Single(p.Evidence.Store.Catalog.Families));
        p.Rebind(plan => plan[field] = Hash("different synthetic rebind pin"));
        await RejectInstalledCatalogAsync(p.Store());
    }

    private static void MutateInstalledCatalog(InstalledAdditiveFixture p, string previous, string fault)
    {
        var receipt = p.LastReceipt!; var head = p.Head!;
        switch (fault)
        {
            case "app": File.AppendAllText(Path.Combine(p.App, "CloudScribe.dll"), "changed"); break;
            case "infrastructure": File.AppendAllText(Path.Combine(p.App, "CloudScribe.Infrastructure.dll"), "changed"); break;
            case "profile": break;
            case "marker": File.AppendAllText(p.Marker, " "); break;
            case "pending-marker":
                var marker = JsonNode.Parse(File.ReadAllBytes(p.Marker))!; marker["LongAudioBuildRebindPending"] = false;
                File.WriteAllBytes(p.Marker, JsonSerializer.SerializeToUtf8Bytes(marker)); break;
            case "legacy": File.AppendAllText(p.Parent, " "); break;
            case "missing-grant": File.Delete(p.PinPath("grants", head.Families[0].GrantSha256)); break;
            case "tampered-grant": File.AppendAllText(p.PinPath("grants", head.Families[0].GrantSha256), " "); break;
            case "missing-receipt": File.Delete(p.PinPath("receipts", head.Families[0].ReceiptSha256)); break;
            case "missing-plan": File.Delete(p.PinPath("plans", receipt.PlanSha256)); break;
            case "missing-proof": File.Delete(p.PinPath("native-proofs", receipt.NativeProofSha256)); break;
            case "missing-source": File.Delete(p.PinPath("source-approvals", receipt.SourceApprovalSha256, "bin")); break;
            case "tampered-source": File.AppendAllText(p.PinPath("source-approvals", receipt.SourceApprovalSha256, "bin"), "changed"); break;
            default: MutateInstalledRegistry(p, previous, fault); break;
        }
    }

    private static void MutateInstalledRegistry(InstalledAdditiveFixture p, string previous, string fault)
    {
        var receipt = p.LastReceipt!; var head = p.Head!;
        switch (fault)
        {
            case "missing-snapshot": File.Delete(p.PinPath("registries", p.HeadSha!)); break;
            case "missing-history": File.Delete(p.PinPath("registries", previous)); break;
            case "tampered-history": File.AppendAllText(p.PinPath("registries", previous), " "); break;
            case "duplicate-sibling": p.Publish(head with { Families = [head.Families[0], head.Families[0]] }); break;
            case "dropped-sibling": p.Publish(head with { Families = [head.Families[1]] }); break;
            case "rolled-back-transaction":
                string state = Path.Combine(p.Root, "transactions", receipt.TransactionId.ToString("N"), "state.json");
                File.WriteAllText(state, File.ReadAllText(state).Replace("Complete", "RolledBack", StringComparison.Ordinal)); break;
            case "missing-transaction": File.Delete(Path.Combine(p.Root, "transactions", receipt.TransactionId.ToString("N"), "state.json")); break;
            default:
                var registry = JsonNode.Parse(File.ReadAllBytes(p.RegistryPath))!;
                if (fault is "null-registry-families") registry["Families"] = null;
                else if (fault is "null-registry-entry") registry["Families"]!.AsArray()[0] = null;
                else registry["UncheckedAllowance"] = 1000000;
                byte[] bytes = Encoding.UTF8.GetBytes(registry.ToJsonString()); p.Pin("registries", bytes); File.WriteAllBytes(p.RegistryPath, bytes); break;
        }
    }
}
