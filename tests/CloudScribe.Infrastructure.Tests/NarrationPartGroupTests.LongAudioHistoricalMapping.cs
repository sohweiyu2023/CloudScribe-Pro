using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing-claim")]
    [InlineData("wrong-scope")]
    [InlineData("amount")]
    [InlineData("duplicate")]
    [InlineData("unclaimed-extra")]
    [InlineData("initial-extra")]
    public async Task LongAudioHistoricalInstalledMappingPreservesSixClaimsButKeepsInitialProvisioningStrict(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var document = await f.Library.CreateAsync("Synthetic historical mapping", "Original synthetic source.", TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var claims = new List<GoogleLongAudioBudgetClaim> { new(p.Request.AttemptId, p.Request.BindingSha256, 1) };
        var map = new Dictionary<string, long>(scope.ApprovedRequestCeilingsUsdCents, StringComparer.Ordinal);
        for (int i = 0; i < 5; i++)
        { string key = Hash("synthetic historical " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)); map.Add(key, 1); claims.Add(new(Guid.NewGuid(), key, 1)); }
        if (fault is "unclaimed-extra") map.Add(Hash("unclaimed synthetic binding"), 1);
        scope = scope with { TotalGoogleCapUsdCents = 2000, ApprovedRequestCeilingsUsdCents = map };
        byte[] scopeBytes = JsonSerializer.SerializeToUtf8Bytes(scope); string scopeSha = Convert.ToHexStringLower(SHA256.HashData(scopeBytes));
        File.WriteAllBytes(p.ScopePath, scopeBytes);
        if (fault is "missing-claim") claims.RemoveAt(claims.Count - 1);
        if (fault is "amount") claims[^1] = claims[^1] with { MaximumUsdCents = 2 };
        if (fault is "duplicate") claims.Add(claims[^1]);
        File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1,
            fault is "wrong-scope" ? new string('a', 64) : scopeSha, claims)));
        string install = Path.GetDirectoryName(p.App)!, markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(markerPath))!;
        marker["GoogleLongAudioScopeSha256"] = JsonSerializer.SerializeToElement(scopeSha);
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        SealSyntheticHistoricalPlan(install, p.LedgerPath, scopeSha, scopeBytes);
        if (fault is "initial-extra") { marker.Remove("GoogleLongAudioScopeSha256"); File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker)); }
        byte[] before = File.ReadAllBytes(p.LedgerPath), beforeScope = File.ReadAllBytes(p.ScopePath);
        var result = await RunLongProvisioningAsync(f, p.App, p.ScopePath, scopeSha, "Inspect");
        Assert.True(fault is "valid" ? result.ExitCode == 0 : result.ExitCode != 0, result.Error);
        Assert.Equal(before, File.ReadAllBytes(p.LedgerPath)); Assert.Equal(beforeScope, File.ReadAllBytes(p.ScopePath));
    }

    private static void SealSyntheticHistoricalPlan(string install, string ledger, string scopeSha, byte[] scopeBytes)
    {
        string directory = Path.Combine(install, "long-audio-provisioning"), markerPath = Path.Combine(install, "cloudscribe-install.json");
        byte[] markerBytes = File.ReadAllBytes(markerPath), ledgerBytes = File.ReadAllBytes(ledger);
        var previous = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(markerBytes)!; previous.Remove("GoogleLongAudioScopeSha256");
        byte[] previousBytes = JsonSerializer.SerializeToUtf8Bytes(previous);
        var plan = JsonSerializer.Deserialize<GoogleLongAudioProvisioningPlan>(File.ReadAllBytes(Path.Combine(directory, "plan.json")))! with
        {
            ScopeSha256 = scopeSha,
            PreviousMarkerSha256 = Convert.ToHexStringLower(SHA256.HashData(previousBytes)),
            NewMarkerSha256 = Convert.ToHexStringLower(SHA256.HashData(markerBytes)),
            InitialLedgerSha256 = Convert.ToHexStringLower(SHA256.HashData(ledgerBytes))
        };
        byte[] planBytes = JsonSerializer.SerializeToUtf8Bytes(plan);
        File.WriteAllBytes(Path.Combine(directory, "plan.json"), planBytes);
        File.WriteAllBytes(Path.Combine(directory, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioProvisioningReceipt(1,
            plan.TransactionId, Convert.ToHexStringLower(SHA256.HashData(planBytes)), "Complete")));
        File.WriteAllBytes(Path.Combine(directory, "scope.json"), scopeBytes); File.WriteAllBytes(Path.Combine(directory, "ledger-initial.json"), ledgerBytes);
        File.WriteAllBytes(Path.Combine(directory, "marker-before.json"), previousBytes); File.WriteAllBytes(Path.Combine(directory, "marker-after.json"), markerBytes);
    }
}
