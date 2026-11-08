using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleCatalogBootstrapTests
{
    [Fact]
    public async Task OrdinaryOpeningReobservesButRetainsCoherentEvidenceAndOriginalExpiry()
    {
        using var f = new DiagnosticFixture();
        var original = await f.ConfigureAsync();
        f.Clear();
        for (int i = 0; i < 3; i++)
        {
            f.Clock.Now = f.Clock.Now.AddMinutes(1);
            var opened = await f.Service.LoadConfiguredForOpeningAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
            Assert.Equal(original.CapabilityEvidence.Id, opened.CapabilityEvidence.Id);
            Assert.Equal(original.ExpiresAtUtc, opened.ExpiresAtUtc);
            Assert.True(opened.RetainedCurrentEvidence);
        }
        Assert.Equal(8, f.Handler.Calls);
        Assert.Equal(3, f.Vault.Writes);
        Assert.Contains(f.Bootstrap.Messages, x => x.Contains("RetainedEvidence", StringComparison.Ordinal));
        f.AssertPrivateValuesAbsent();
        var refreshed = await f.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(original.CapabilityEvidence.Id, refreshed.CapabilityEvidence.Id);
        Assert.False(refreshed.RetainedCurrentEvidence);
        Assert.Contains(f.Bootstrap.Messages, x => x.Contains("NewEvidence", StringComparison.Ordinal));
        f.AssertPrivateValuesAbsent();
    }

    [Theory]
    [InlineData("captured-credential")]
    [InlineData("captured-endpoint")]
    [InlineData("captured-region")]
    [InlineData("captured-origin")]
    [InlineData("capability-future")]
    [InlineData("capability-no-catalog")]
    [InlineData("capability-no-synthesis")]
    [InlineData("missing-capability")]
    [InlineData("expired-capability")]
    [InlineData("missing-project")]
    [InlineData("expired-project")]
    [InlineData("project-snapshot")]
    [InlineData("project-revision")]
    [InlineData("project-credential")]
    [InlineData("project-window")]
    [InlineData("missing-voice")]
    [InlineData("voice-model")]
    [InlineData("voice-project")]
    [InlineData("voice-credential")]
    [InlineData("voice-endpoint")]
    [InlineData("voice-region")]
    [InlineData("voice-origin")]
    [InlineData("voice-provenance")]
    [InlineData("voice-window")]
    [InlineData("voice-denied")]
    [InlineData("account-revision")]
    [InlineData("account-region")]
    [InlineData("catalog-metadata")]
    [InlineData("probe-provenance")]
    public async Task OrdinaryOpeningNeverRetainsMissingStaleOrMismatchedEvidence(string change)
    {
        using var f = new DiagnosticFixture();
        var original = await f.ConfigureAsync();
        ChangeOpeningEvidence(f, change);
        var opened = await f.Service.LoadConfiguredForOpeningAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.NotEqual(original.CapabilityEvidence.Id, opened.CapabilityEvidence.Id);
        Assert.False(opened.RetainedCurrentEvidence);
        new GoogleGenerationProductionEvidence(opened.Account, opened.CapabilityEvidence).Validate(f.Clock.Now);
        Assert.Equal(4, f.Handler.Calls);
        Assert.Equal(3, f.Vault.Writes);
    }

    private static void ChangeOpeningEvidence(DiagnosticFixture f, string change)
    {
        if (change.StartsWith("captured-", StringComparison.Ordinal) || change.StartsWith("capability-", StringComparison.Ordinal))
        {
            ChangeOpeningCapability(f, change);
            return;
        }
        var project = f.Stores.Project!;
        if (change.StartsWith("voice-", StringComparison.Ordinal))
        {
            f.Stores.Authorization = ChangeOpeningVoice(f.Stores.Authorization!, change);
            return;
        }
        switch (change)
        {
            case "missing-capability": f.Stores.Capability = null; break;
            case "expired-capability": f.Clock.Now = f.Clock.Now.AddMinutes(31); break;
            case "missing-project": f.Stores.Project = null; break;
            case "expired-project": f.Stores.Project = project with { ExpiresAtUtc = f.Clock.Now }; break;
            case "project-snapshot": f.Stores.Project = project with { CapabilityEvidenceId = Guid.NewGuid().ToString("D") }; break;
            case "project-revision": f.Stores.Project = project with { AccountRevision = project.AccountRevision + 1 }; break;
            case "project-credential": f.Stores.Project = project with { CredentialReferenceId = "changed" }; break;
            case "project-window": f.Stores.Project = project with { ExpiresAtUtc = project.ExpiresAtUtc.AddSeconds(1) }; break;
            case "missing-voice": f.Stores.Authorization = null; break;
            case "account-revision": case "account-region": ChangeOpeningAccount(f, change); break;
            case "catalog-metadata": f.Handler.DifferentCatalogMetadata = true; break;
            case "probe-provenance": f.Handler.DifferentProbeBody = true; break;
            default: throw new InvalidOperationException("Unknown synthetic change.");
        }
    }

    private static void ChangeOpeningCapability(DiagnosticFixture f, string change)
    {
        var current = f.Stores.Capability!;
        var r = current.Snapshot.Account;
        var reference = new ProviderAccountReference(r.ProviderStableId, r.AccountId, r.DisplayName,
            string.Equals(change, "captured-credential", StringComparison.Ordinal) ? new CredentialReference("changed") : r.CredentialReference,
            string.Equals(change, "captured-endpoint", StringComparison.Ordinal) ? "changed" : r.EndpointId,
            string.Equals(change, "captured-region", StringComparison.Ordinal) ? "changed" : r.RegionId,
            string.Equals(change, "captured-origin", StringComparison.Ordinal) ? new Uri("https://changed.googleapis.com") : r.EndpointOrigin);
        var capabilities = current.Snapshot.Capabilities.Where(c => !(string.Equals(change, "capability-no-catalog", StringComparison.Ordinal)
            && string.Equals(c.CapabilityId, GoogleTextToSpeechCatalogBootstrapService.VoiceCatalogCapabilityId, StringComparison.Ordinal))
            && !(string.Equals(change, "capability-no-synthesis", StringComparison.Ordinal)
            && string.Equals(c.CapabilityId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)));
        var captured = string.Equals(change, "capability-future", StringComparison.Ordinal) ? f.Clock.Now.AddSeconds(1) : current.Snapshot.CapturedAtUtc;
        f.Stores.Capability = new(current.Id, new(reference, captured, current.Snapshot.ProvenanceId, capabilities), current.ExpiresAtUtc);
    }

    private static GoogleGenerationProjectAuthorizationEvidence ChangeOpeningVoice(GoogleGenerationProjectAuthorizationEvidence voice, string change) => change switch
    {
        "voice-model" => voice with { ModelId = "changed" },
        "voice-project" => voice with { ProjectId = "changed" },
        "voice-credential" => voice with { CredentialReferenceId = "changed" },
        "voice-endpoint" => voice with { EndpointId = "changed" },
        "voice-region" => voice with { RegionId = "changed" },
        "voice-origin" => voice with { EndpointOrigin = "https://changed.googleapis.com" },
        "voice-provenance" => voice with { CapabilityProvenanceId = "changed" },
        "voice-window" => voice with { ExpiresAtUtc = voice.ExpiresAtUtc.AddSeconds(1) },
        "voice-denied" => voice with { Authorized = false },
        _ => throw new InvalidOperationException("Unknown synthetic voice change."),
    };

    private static void ChangeOpeningAccount(DiagnosticFixture f, string change)
    {
        var account = f.Stores.Account!;
        var r = account.Reference;
        var reference = string.Equals(change, "account-region", StringComparison.Ordinal)
            ? new ProviderAccountReference(r.ProviderStableId, r.AccountId, r.DisplayName, r.CredentialReference, r.EndpointId, "changed-region", r.EndpointOrigin) : r;
        f.Stores.Account = new ProviderAccountSnapshot(reference, true, account.Revision + 1, account.CreatedAtUtc, f.Clock.Now);
    }

    [Fact]
    public async Task OrdinaryOpeningRejectsDiagnosticModeWithoutClaimOrProviderCall()
    {
        using var f = new CompletionFixture();
        await f.InitializeAsync();
        int calls = f.Handler.Calls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.LoadConfiguredForOpeningAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Equal(calls, f.Handler.Calls);
        Assert.Equal(0, f.Runner.Refreshes);
        Assert.Null(f.Runner.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningRejectsConnectionChangeDuringRetainedEvidenceValidation(bool capabilityChanged)
    {
        using var f = new DiagnosticFixture();
        var original = await f.ConfigureAsync();
        f.Stores.BeforeVoiceLoad = () =>
        {
            if (capabilityChanged) f.Stores.Capability = new(Guid.NewGuid(), original.CapabilityEvidence.Snapshot, original.ExpiresAtUtc);
            else ChangeOpeningAccount(f, "account-revision");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.LoadConfiguredForOpeningAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Equal(original.ExpiresAtUtc, f.Stores.Capability!.ExpiresAtUtc);
        Assert.Equal(4, f.Handler.Calls);
        Assert.Equal(capabilityChanged, f.Stores.Capability.Id != original.CapabilityEvidence.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticActivationDuringOrdinaryOpeningDoesNotClaimRefreshOrPersistEvidence(bool duringRetention)
    {
        using var f = new CompletionFixture();
        await f.InitializeAsync();
        var original = f.Stores.Capability;
        bool active = false;
        var service = f.CreateService(new CompletionWorkflowGate(() => active ? f.Configuration : null, f.Runner));
        if (duringRetention) f.Stores.BeforeVoiceLoad = () => { active = true; f.Stores.Authorization = null; };
        else f.Handler.BeforeProbeResponse = () => active = true;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LoadConfiguredForOpeningAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Same(original, f.Stores.Capability);
        Assert.Equal(0, f.Runner.Refreshes);
        Assert.Null(f.Runner.Pending);
    }

    [Fact]
    public async Task CancelledOrAccountChangedOpeningCannotReplaceCurrentEvidence()
    {
        using var f = new DiagnosticFixture();
        var original = await f.ConfigureAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.LoadConfiguredForOpeningAsync(Guid.NewGuid(), cancelled.Token));
        Assert.Same(original.CapabilityEvidence, f.Stores.Capability);
        f.Handler.BeforeProbeResponse = () => ChangeOpeningAccount(f, "account-revision");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.LoadConfiguredForOpeningAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Same(original.CapabilityEvidence, f.Stores.Capability);
    }
}
