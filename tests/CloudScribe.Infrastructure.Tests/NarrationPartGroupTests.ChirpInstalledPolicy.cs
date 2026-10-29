using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("hash")]
    [InlineData("app")]
    [InlineData("module")]
    [InlineData("profile")]
    [InlineData("expired")]
    [InlineData("count")]
    [InlineData("cap")]
    [InlineData("accepted-range")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("null-article")]
    [InlineData("null-proposal")]
    [InlineData("oversized")]
    public async Task ChirpOnlyExactInstallerPinnedPolicyCanSupplyFreshAuthority(string failure)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        string install = Path.Combine(f.Root, "synthetic-install"), app = Path.Combine(install, "app"); Directory.CreateDirectory(app);
        string appFile = Path.Combine(app, "CloudScribe.dll"), moduleFile = Path.Combine(app, "CloudScribe.Infrastructure.dll");
        await File.WriteAllTextAsync(appFile, "inert synthetic application", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(moduleFile, "inert synthetic infrastructure", TestContext.Current.CancellationToken);
        var policy = e.Admission.Policy! with { AppSha256 = Hash("inert synthetic application"), InfrastructureSha256 = Hash("inert synthetic infrastructure") };
        policy = failure switch
        {
            "profile" => policy with { ProfileRootSha256 = new string('d', 64) },
            "expired" => policy with { ExpiresAtUtc = e.Clock.Now.AddSeconds(-1) },
            "count" => policy with { MaximumNewAttempts = 0 },
            "cap" => policy with { MaximumAdditionalReservationMinorUnits = 301 },
            "accepted-range" => policy with { Articles = [policy.Articles[0] with { AllowedSegments = [new(99999, 1)] }] },
            "null-article" => policy with { Articles = [null!] },
            "null-proposal" => policy with { Articles = [policy.Articles[0] with { Proposal = null! }] },
            _ => policy,
        };
        string json = JsonSerializer.Serialize(policy);
        if (string.Equals(failure, "duplicate", StringComparison.Ordinal)) json = "{\"SchemaVersion\":1," + json[1..];
        if (string.Equals(failure, "unknown", StringComparison.Ordinal)) json = "{\"CallerApprovedUnlimited\":true," + json[1..];
        if (string.Equals(failure, "oversized", StringComparison.Ordinal)) json += new string(' ', 8 * 1024 * 1024);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string policyFile = Path.Combine(install, "chirp-article-admission-policy.json"), marker = Path.Combine(install, "cloudscribe-install.json");
        await File.WriteAllBytesAsync(policyFile, bytes, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new
        {
            Product = "CloudScribe Pro",
            policy.AppSha256,
            ChirpArticlePolicySha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))
        }), TestContext.Current.CancellationToken);
        if (string.Equals(failure, "hash", StringComparison.Ordinal)) await File.AppendAllTextAsync(policyFile, " ", TestContext.Current.CancellationToken);
        if (string.Equals(failure, "app", StringComparison.Ordinal)) await File.WriteAllTextAsync(appFile, "changed", TestContext.Current.CancellationToken);
        if (string.Equals(failure, "module", StringComparison.Ordinal)) await File.WriteAllTextAsync(moduleFile, "changed", TestContext.Current.CancellationToken);
        var store = new InstalledChirpArticleAdmissionStore(app, f.Paths, e.Clock);
        var admitted = await store.LoadVerifiedAsync(TestContext.Current.CancellationToken);
        if (string.Equals(failure, "valid", StringComparison.Ordinal))
        {
            Assert.NotNull(admitted); Assert.Equal(policy.SourceApprovalSha256, admitted.SourceApprovalSha256);
            await File.WriteAllTextAsync(marker, "{}", TestContext.Current.CancellationToken);
            Assert.Null(await store.LoadVerifiedAsync(TestContext.Current.CancellationToken));
        }
        else Assert.Null(admitted);
    }
}
