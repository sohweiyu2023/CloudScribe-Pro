using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleCapabilityRefreshPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewerCapabilityObservationIsAccepted()
    {
        var account = Account();
        var current = Snapshot(Now.AddMinutes(-10), "cap:v1");
        var candidate = Snapshot(Now.AddMinutes(-2), "cap:v2");

        var result = GoogleCapabilityRefreshPolicy.Evaluate(account, current, candidate, Now);

        Assert.True(result.Accepted);
        Assert.Equal("capability-refresh-accepted", result.DiagnosticCode);
        Assert.Same(candidate, result.Snapshot);
    }

    [Fact]
    public void OlderOrConflictingObservationIsRejected()
    {
        var account = Account();
        var current = Snapshot(Now.AddMinutes(-5), "cap:v1");

        var older = GoogleCapabilityRefreshPolicy.Evaluate(
            account,
            current,
            Snapshot(Now.AddMinutes(-6), "cap:v0"),
            Now);
        var conflict = GoogleCapabilityRefreshPolicy.Evaluate(
            account,
            current,
            Snapshot(Now.AddMinutes(-5), "cap:other"),
            Now);

        Assert.False(older.Accepted);
        Assert.Equal("capability-observation-regressed", older.DiagnosticCode);
        Assert.False(conflict.Accepted);
        Assert.Equal("capability-conflicting-provenance", conflict.DiagnosticCode);
    }

    [Fact]
    public void AccountMismatchAndStaleCandidateFailClosed()
    {
        var account = Account();
        var mismatch = Snapshot(Now.AddMinutes(-1), "cap:v2") with { AccountId = "other" };
        var stale = Snapshot(Now.AddHours(-2), "cap:old") with { ExpiresAtUtc = Now.AddMinutes(-1) };

        Assert.Equal(
            "capability-account-mismatch",
            GoogleCapabilityRefreshPolicy.Evaluate(account, null, mismatch, Now).DiagnosticCode);
        Assert.Equal(
            "capability-candidate-stale",
            GoogleCapabilityRefreshPolicy.Evaluate(account, null, stale, Now).DiagnosticCode);
    }

    private static GoogleGenerationAccount Account() => new(
        "acct:google",
        "credential:google",
        new Uri("https://texttospeech.googleapis.com/"),
        "global");

    private static GoogleCapabilitySnapshot Snapshot(DateTimeOffset observedAt, string provenance) => new(
        "acct:google",
        provenance,
        observedAt,
        observedAt.AddHours(1),
        new HashSet<string>(StringComparer.Ordinal) { "en-US-Neural2-A" },
        new HashSet<string>(StringComparer.Ordinal) { "LINEAR16" },
        32_768);
}
