using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage4ProviderFoundationTests
{
    [Fact]
    public async Task FakeProviderRemainsLazyAndReturnsExplicitCapabilitySnapshot()
    {
        FakeFactory factory = new();
        ProviderFactoryRegistry registry = new([factory]);
        Assert.Equal(0, factory.CreateCount);
        Assert.True(registry.TryGetFactory("fake", out IProviderAdapterFactory? resolved));
        await using IProviderAdapter adapter = await resolved!.CreateAdapterAsync("test-account", TestContext.Current.CancellationToken);
        Assert.Equal(1, factory.CreateCount);
        IProviderCapabilitySource capabilitySource = Assert.IsAssignableFrom<IProviderCapabilitySource>(adapter);
        ProviderCapabilitySnapshot snapshot = await capabilitySource.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.True(snapshot.GetCapability("synthesize-speech").IsUsable);
        ProviderCapability absent = snapshot.GetCapability("multi-speaker");
        Assert.Equal(ProviderCapabilityState.Unknown, absent.State);
        Assert.NotNull(absent.DisabledReason);
    }

    [Fact]
    public void ProviderNeutralReferencesKeepEverySelectionAndPolicyEvidenceExplicit()
    {
        ProviderEndpointReference endpoint = new("default", "global");
        ProviderModelReference model = new("tts-model", "models/acme:v1", ProviderLifecycleState.Available, "snapshot-2026-08");
        ProviderAliasReference alias = new("models/acme:v1", model.StableId, "test-fixture:alias-v1");
        ProviderVoiceReference voice = new("voice-a", "voices/en-US/A", model.StableId);
        ProviderOperationReference operation = new("synthesize-speech", ProviderLifecycleState.Available);
        ProviderGovernanceReference governance = new("standard-policy", "test-fixture:governance-v1");
        ProviderDataHandlingReference dataHandling = new("no-training", "test-fixture:data-handling-v1");

        Assert.Equal("default", endpoint.EndpointId);
        Assert.Equal("global", endpoint.RegionId);
        Assert.Equal("models/acme:v1", model.ExactApiAlias);
        Assert.Equal(model.StableId, alias.TargetStableId);
        Assert.Equal("voices/en-US/A", voice.ExactProviderVoiceId);
        Assert.Equal("synthesize-speech", operation.StableId);
        Assert.Equal("standard-policy", governance.ProfileId);
        Assert.Equal("no-training", dataHandling.ProfileId);
        Assert.Throws<ArgumentException>(() => new ProviderModelReference("Implicit Default", "model", ProviderLifecycleState.Available));
        Assert.Throws<ArgumentException>(() => new ProviderAliasReference("model", "Implicit Default", "test"));
    }

    [Fact]
    public void AccountAndCapabilityIdentifiersCannotHideControlOrImplicitSwitches()
    {
        CredentialReference credential = new("fake.test-account.api-key");
        ProviderAccountReference account = new("fake", "test-account", "Test Account", credential, "default", "global");
        Assert.Equal("fake", account.ProviderStableId);
        Assert.Throws<ArgumentException>(() => new ProviderAccountReference("Fake", "test-account", "Test", credential));
        Assert.Throws<ArgumentException>(() => new ProviderCapability(
            "synthesize-speech", ProviderCapabilityState.Unsupported, ProviderLifecycleState.Available));
    }

    private sealed class FakeFactory : IProviderAdapterFactory
    {
        public int CreateCount { get; private set; }
        public ProviderDescriptor Descriptor { get; } = new("fake", "Deterministic Fake", false, false);

        public ValueTask<IProviderAdapter> CreateAdapterAsync(string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            return ValueTask.FromResult<IProviderAdapter>(new FakeAdapter(accountId, Descriptor));
        }
    }

    private sealed class FakeAdapter : IProviderAdapter, IProviderCapabilitySource
    {
        private readonly string accountId;

        public FakeAdapter(string accountId, ProviderDescriptor descriptor)
        {
            this.accountId = accountId;
            Descriptor = descriptor;
        }

        public ProviderDescriptor Descriptor { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<ProviderCapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderAccountReference account = new(Descriptor.StableId, accountId, "Deterministic Fake", null, "default", "global");
            ProviderCapabilitySnapshot snapshot = new(
                account,
                new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
                "test-fixture:stage4-provider-v1",
                [new ProviderCapability("synthesize-speech", ProviderCapabilityState.Supported, ProviderLifecycleState.Available)]);
            return ValueTask.FromResult(snapshot);
        }
    }
}
