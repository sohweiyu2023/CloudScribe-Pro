using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class InstalledRecoveryAdmissionStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyInstallerPinnedCompatiblePolicySuppliesAdmittedEvidenceAndRechecksRevocation(bool trailingSeparator)
    {
        using var f = new Fixture(trailingSeparator);
        Assert.Null(await f.Store.LoadVerifiedPolicyAsync(TestContext.Current.CancellationToken));
        f.Admit(f.Policy);
        Assert.Equal("synthetic-admitted-evidence", Assert.Single(await f.Store.ListHistoricalEvidenceIdsAsync(TestContext.Current.CancellationToken)));
        var anchor = await f.Store.LoadAdmittedAsync("synthetic-admitted-evidence", TestContext.Current.CancellationToken);
        Assert.NotNull(anchor);
        Assert.Equal(anchor.IndependentlyAdmittedSha256, Hash(anchor.AnchorBytes.Span));
        Assert.Null(await f.Store.LoadAdmittedAsync("caller-selected-unadmitted-file", TestContext.Current.CancellationToken));
        var prior = Assert.Single(f.Policy.Predecessors);
        Assert.Equal(prior, await f.Store.ResolveAsync(prior.AccountId, prior.OperationId, prior.HistoricalKey,
            prior.HistoricalStateSha256, TestContext.Current.CancellationToken));
        Assert.Null(await f.Store.ResolveAsync(prior.AccountId, prior.OperationId, prior.HistoricalKey, new string('d', 64), TestContext.Current.CancellationToken));
        File.WriteAllText(f.MarkerPath, "{}");
        Assert.Null(await f.Store.LoadAdmittedAsync("synthetic-admitted-evidence", TestContext.Current.CancellationToken));
        Assert.Empty(await f.Store.ListHistoricalEvidenceIdsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("build")]
    [InlineData("profile")]
    [InlineData("expired")]
    [InlineData("scope")]
    [InlineData("schema")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("oversized")]
    [InlineData("module-changed")]
    [InlineData("app-policy-build")]
    [InlineData("app-changed-marker-refreshed")]
    public async Task InvalidOrUnreviewedPolicyCannotBecomeRuntimeAuthority(string failure)
    {
        using var f = new Fixture();
        var p = failure switch
        {
            "build" => f.Policy with { InfrastructureSha256 = new string('b', 64) },
            "app-policy-build" => f.Policy with { AppSha256 = new string('b', 64) },
            "profile" => f.Policy with { ProfileRootSha256 = new string('b', 64) },
            "expired" => f.Policy with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "scope" => f.Policy with { AccountId = "another-account" },
            "schema" => f.Policy with { SchemaVersion = 2 },
            _ => f.Policy,
        };
        string json = JsonSerializer.Serialize(p);
        if (string.Equals(failure, "duplicate", StringComparison.Ordinal)) json = "{\"SchemaVersion\":1," + json[1..];
        if (string.Equals(failure, "unknown", StringComparison.Ordinal)) json = "{\"UnreviewedTrustOverride\":true," + json[1..];
        if (string.Equals(failure, "oversized", StringComparison.Ordinal)) json += new string(' ', 1024 * 1024);
        f.AdmitBytes(System.Text.Encoding.UTF8.GetBytes(json));
        if (string.Equals(failure, "hash", StringComparison.Ordinal)) File.AppendAllText(f.PolicyPath, " ");
        if (string.Equals(failure, "module-changed", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(f.AppDirectory, "CloudScribe.Infrastructure.dll"), "different-synthetic-module");
        if (string.Equals(failure, "app-changed-marker-refreshed", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(f.AppDirectory, "CloudScribe.dll"), "different-synthetic-app-module");
            f.AdmitBytes(System.Text.Encoding.UTF8.GetBytes(json));
        }
        Assert.Null(await f.Store.LoadVerifiedPolicyAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await f.Store.ListHistoricalEvidenceIdsAsync(TestContext.Current.CancellationToken));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudscribe-admission-store-tests", Guid.NewGuid().ToString("N"));
        public Fixture(bool trailingSeparator = false)
        {
            AppDirectory = Path.Combine(_root, "install", "app"); Directory.CreateDirectory(AppDirectory);
            File.WriteAllText(Path.Combine(AppDirectory, "CloudScribe.dll"), "synthetic-app-module");
            File.WriteAllText(Path.Combine(AppDirectory, "CloudScribe.Infrastructure.dll"), "synthetic-infrastructure-module");
            var paths = new AppPaths(Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = Path.Combine(_root, "profile") }));
            paths.EnsureDirectories(); Store = new(AppDirectory + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : string.Empty), paths, TimeProvider.System);
            var expiry = DateTimeOffset.UtcNow.AddHours(1);
            var anchor = new ArticleHistoricalAudioAnchor(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), 1,
                Guid.NewGuid(), Guid.NewGuid(), new string('b', 64), "synthetic-account", GoogleGenerationProvider.SynthesizeOperationStableId,
                "synthetic-key", Guid.NewGuid().ToString("N"), new string('c', 64), 100, new string('d', 64), 128,
                "synthetic-voice", "en-US", new string('e', 64));
            var prior = new GoogleRecoveryPredecessorEvidence("synthetic-logical-item", anchor.AccountId, anchor.OperationId,
                anchor.IdempotencyKey, new string('f', 64), "synthetic-project", anchor.DocumentId, anchor.RevisionId,
                anchor.TextSha256, anchor.PayloadSha256, new string('a', 64), expiry);
            Policy = new(1, Hash(File.ReadAllBytes(Path.Combine(AppDirectory, "CloudScribe.dll"))), Hash(File.ReadAllBytes(Path.Combine(AppDirectory, "CloudScribe.Infrastructure.dll"))),
                InstalledRecoveryAdmissionStore.ProfileHash(paths.RootDirectory), "synthetic-account", "synthetic-project", "USD", 2, expiry,
                new(250, 1000, 10, 80, new string('a', 64), [], [], []), [new("synthetic-admitted-evidence", anchor)], [prior]);
        }
        public string AppDirectory { get; }
        public string MarkerPath => Path.Combine(_root, "install", "cloudscribe-install.json");
        public string PolicyPath => Path.Combine(_root, "install", "recovery-admission-policy.json");
        public RecoveryAdmissionPolicy Policy { get; }
        public InstalledRecoveryAdmissionStore Store { get; }
        public void Admit(RecoveryAdmissionPolicy p) => AdmitBytes(JsonSerializer.SerializeToUtf8Bytes(p));
        public void AdmitBytes(byte[] bytes)
        {
            // Synthetic privileged installer fixture, never a runtime admission API.
            File.WriteAllBytes(PolicyPath, bytes);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new
            {
                Product = "CloudScribe Pro",
                Version = 1,
                AppSha256 = Hash(File.ReadAllBytes(Path.Combine(AppDirectory, "CloudScribe.dll"))),
                RecoveryPolicySha256 = Hash(bytes)
            }));
        }
        public void Dispose()
        {
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-admission-store-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                || Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidOperationException("Unexpected synthetic admission cleanup root.");
            Directory.Delete(_root, recursive: true);
        }
    }
}
