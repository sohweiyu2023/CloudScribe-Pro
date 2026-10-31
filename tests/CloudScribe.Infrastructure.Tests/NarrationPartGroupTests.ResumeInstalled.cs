using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("expired")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("unreviewed-pin")]
    [InlineData("changed-build")]
    [InlineData("wrong-profile")]
    [InlineData("new-funds")]
    [InlineData("two-dispatches")]
    [InlineData("old-approval")]
    [InlineData("private-field")]
    [InlineData("logger-failure")]
    public async Task InstalledResumeStoreRequiresExactPinBuildProfileFreshScopeAndSafeReadback(string change)
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f); var policy = r.Admission.Policy!;
        string install = Path.Combine(f.Root, "synthetic-install"), app = Path.Combine(install, "app"); Directory.CreateDirectory(app);
        await File.WriteAllTextAsync(Path.Combine(app, "CloudScribe.dll"), "inert synthetic app", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(app, "CloudScribe.Infrastructure.dll"), "inert synthetic infrastructure", TestContext.Current.CancellationToken);
        policy = policy with { AppSha256 = Hash("inert synthetic app"), InfrastructureSha256 = Hash("inert synthetic infrastructure") };
        policy = change switch
        {
            "wrong-profile" => policy with { ProfileRootSha256 = new string('0', 64) },
            "new-funds" => policy with { MaximumAdditionalReservationMinorUnits = 1 },
            "two-dispatches" => policy with { MaximumExistingDispatches = 2 },
            "old-approval" => policy with { SourceApprovalSha256 = policy.HistoricalPolicy.SourceApprovalSha256 },
            _ => policy,
        };
        string json = JsonSerializer.Serialize(policy);
        if (string.Equals(change, "malformed", StringComparison.Ordinal)) json = "{synthetic malformed";
        if (string.Equals(change, "duplicate", StringComparison.Ordinal)) json = json.Insert(1, "\"SchemaVersion\":1,");
        if (change is "private-field" or "logger-failure") json = json.Insert(1, "\"private_key\":\"synthetic-never-log-token\",");
        await File.WriteAllTextAsync(Path.Combine(install, "chirp-reservation-resume-admission.json"), json, TestContext.Current.CancellationToken);
        string pin = string.Equals(change, "unreviewed-pin", StringComparison.Ordinal) ? new string('0', 64) : Hash(json);
        await File.WriteAllTextAsync(Path.Combine(install, "cloudscribe-install.json"), JsonSerializer.Serialize(new
        { Product = "CloudScribe Pro", AppSha256 = policy.AppSha256, ChirpReservationResumePolicySha256 = pin }), TestContext.Current.CancellationToken);
        if (string.Equals(change, "changed-build", StringComparison.Ordinal)) await File.WriteAllTextAsync(Path.Combine(app, "CloudScribe.dll"), "changed inert bytes", TestContext.Current.CancellationToken);
        if (string.Equals(change, "expired", StringComparison.Ordinal)) r.Old.Clock.Now = policy.ExpiresAtUtc;
        var logger = new ResumeStoreLogger(string.Equals(change, "logger-failure", StringComparison.Ordinal));
        var store = new InstalledChirpReservationResumeAdmissionStore(app, f.Paths, r.Old.Clock, logger);
        var loaded = await store.LoadVerifiedAsync(TestContext.Current.CancellationToken);
        if (string.Equals(change, "valid", StringComparison.Ordinal)) Assert.Equal(ChirpPolicyValidation.Hash(policy), ChirpPolicyValidation.Hash(Assert.IsType<ChirpReservationResumePolicy>(loaded)));
        else Assert.Null(loaded);
        _ = await store.LoadVerifiedAsync(TestContext.Current.CancellationToken);
        Assert.True(logger.Messages.Count <= 1); Assert.Null(logger.Exception);
        string logged = string.Join('\n', logger.Messages);
        Assert.DoesNotContain("synthetic-never-log-token", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("private_key", logged, StringComparison.Ordinal);
        if (string.Equals(change, "expired", StringComparison.Ordinal)) Assert.Contains("ResumeExpired", logged, StringComparison.Ordinal);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    private sealed class ResumeStoreLogger(bool throws) : ILogger<InstalledChirpReservationResumeAdmissionStore>
    {
        public List<string> Messages { get; } = [];
        public Exception? Exception { get; private set; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (throws) throw new IOException("synthetic logger failure containing private text");
            Exception = exception; Messages.Add(formatter(state, exception));
        }
    }
}
