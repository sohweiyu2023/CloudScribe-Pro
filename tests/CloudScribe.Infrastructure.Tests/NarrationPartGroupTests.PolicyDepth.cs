using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(25)]
    [InlineData(64)]
    public void NativeArticleEvidenceParserAcceptsStandardFiniteDepth(int depth)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('[', depth) + "0" + new string(']', depth));
        Assert.Equal(JsonValueKind.Array, ParseDepthEvidence(bytes).ValueKind);
    }

    [Fact]
    public void NativeArticleEvidenceParserRejectsDepth65AndNestedDuplicateMembers()
    {
        byte[] tooDeep = Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65));
        var error = Assert.Throws<TargetInvocationException>(() => ParseDepthEvidence(tooDeep));
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        byte[] duplicate = Encoding.UTF8.GetBytes(new string('[', 25) + "{\"same\":1,\"same\":2}" + new string(']', 25));
        error = Assert.Throws<TargetInvocationException>(() => ParseDepthEvidence(duplicate));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("pin")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    public async Task NativeInstalledStoreReadsNestedContinuityButRetainsTrustAndSizeChecks(string change)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        string install = Path.Combine(f.Paths.RootDirectory, "synthetic-depth-install"); string app = Path.Combine(install, "app");
        Directory.CreateDirectory(app);
        byte[] appBytes = "synthetic depth app"u8.ToArray(), infraBytes = "synthetic depth infra"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(app, "CloudScribe.dll"), appBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(app, "CloudScribe.Infrastructure.dll"), infraBytes, TestContext.Current.CancellationToken);
        var policy = e.Admission.Policy! with
        {
            AppSha256 = DepthHash(appBytes),
            InfrastructureSha256 = DepthHash(infraBytes),
            ProfileRootSha256 = InstalledRecoveryAdmissionStore.ProfileHash(f.Paths.RootDirectory)
        };
        for (int i = 0; i < 10; i++) policy = policy with
        {
            SourceApprovalSha256 = DepthHash(Encoding.UTF8.GetBytes("synthetic depth approval " + i)),
            CompletionContinuity = new(1, e.Document.Id, new string('b', 64), policy)
        };
        ChirpPolicyValidation.RequireValid(policy, e.Clock.Now);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy);
        using (var document = JsonDocument.Parse(bytes)) Assert.True(DepthOf(document.RootElement) > 24);
        TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_DEEP_ARTICLE_POLICY_BASE64=" + Convert.ToBase64String(bytes));
        bytes = change switch
        {
            "unknown" => Encoding.UTF8.GetBytes("{\"UnreviewedAuthority\":true," + Encoding.UTF8.GetString(bytes)[1..]),
            "duplicate" => Encoding.UTF8.GetBytes("{\"SchemaVersion\":3," + Encoding.UTF8.GetString(bytes)[1..]),
            "oversize" => new byte[8 * 1024 * 1024 + 1],
            _ => bytes
        };
        await File.WriteAllBytesAsync(Path.Combine(install, "chirp-article-admission-policy.json"), bytes, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(install, "cloudscribe-install.json"), JsonSerializer.Serialize(new
        { Product = "CloudScribe Pro", AppSha256 = policy.AppSha256, ChirpArticlePolicySha256 = change is "pin" ? new string('0', 64) : DepthHash(bytes) }), TestContext.Current.CancellationToken);
        var loaded = await new InstalledChirpArticleAdmissionStore(app, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken);
        if (change is "valid") Assert.Equal(ChirpPolicyValidation.Hash(policy), ChirpPolicyValidation.Hash(Assert.IsType<ChirpArticleAdmissionPolicy>(loaded)));
        else Assert.Null(loaded);
    }

    private static string DepthHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static int DepthOf(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => 1 + node.EnumerateObject().Select(x => DepthOf(x.Value)).DefaultIfEmpty(0).Max(),
        JsonValueKind.Array => 1 + node.EnumerateArray().Select(DepthOf).DefaultIfEmpty(0).Max(),
        _ => 0
    };
    private static JsonElement ParseDepthEvidence(byte[] bytes) => (JsonElement)typeof(ChirpArticleService).Assembly
        .GetType("CloudScribe.Infrastructure.Generation.ChirpPolicyFiles", true)!.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!
        .MakeGenericMethod(typeof(JsonElement)).Invoke(null, [bytes])!;
}
