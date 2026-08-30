using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleGenerationProductionTransportFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly Uri EndpointOrigin = new("https://speech.example.test/", UriKind.Absolute);

    [Fact]
    public void CreateMaterializesValidatedAccountAndPinnedTransport()
    {
        GoogleGenerationProductionEvidence evidence = CreateEvidence(EndpointOrigin, Now.AddHours(1));
        var factory = new GoogleGenerationProductionTransportFactory(
            new HttpClient(),
            new NeverResolvingCredentialResolver(),
            new FixedTimeProvider(Now));

        GoogleGenerationProductionTransport result = factory.Create(evidence);

        Assert.Equal("primary", result.Account.AccountId);
        Assert.Equal("google.primary", result.Account.CredentialReferenceId);
        Assert.Equal(EndpointOrigin, result.Account.Endpoint);
        Assert.Equal("global", result.Account.Region);
        Assert.NotNull(result.Transport);
    }

    [Fact]
    public void CreateRejectsEvidenceThatBecameStaleBeforeMaterialization()
    {
        GoogleGenerationProductionEvidence evidence = CreateEvidence(EndpointOrigin, Now);
        var factory = new GoogleGenerationProductionTransportFactory(
            new HttpClient(),
            new NeverResolvingCredentialResolver(),
            new FixedTimeProvider(Now));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => factory.Create(evidence));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateRejectsEndpointSubstitutionBeforeTransportConstruction()
    {
        ProviderAccountSnapshot current = CreateAccount(EndpointOrigin);
        ProviderAccountSnapshot captured = CreateAccount(new Uri("https://old-speech.example.test/", UriKind.Absolute));
        GoogleGenerationProductionEvidence evidence = new(
            current,
            CreateCapability(captured.Reference, Now.AddHours(1)));
        var factory = new GoogleGenerationProductionTransportFactory(
            new HttpClient(),
            new NeverResolvingCredentialResolver(),
            new FixedTimeProvider(Now));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => factory.Create(evidence));

        Assert.Contains("changed after capability evidence", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GoogleGenerationProductionEvidence CreateEvidence(Uri endpointOrigin, DateTimeOffset expiresAtUtc)
    {
        ProviderAccountSnapshot account = CreateAccount(endpointOrigin);
        return new GoogleGenerationProductionEvidence(account, CreateCapability(account.Reference, expiresAtUtc));
    }

    private static ProviderAccountSnapshot CreateAccount(Uri endpointOrigin)
    {
        ProviderAccountReference reference = new(
            GoogleGenerationProvider.StableProviderId,
            "primary",
            "Google primary",
            new CredentialReference("google.primary"),
            "google-tts-v1",
            "global",
            endpointOrigin);
        return new ProviderAccountSnapshot(reference, true, 1, Now.AddDays(-1), Now.AddMinutes(-10));
    }

    private static StoredProviderCapabilitySnapshot CreateCapability(
        ProviderAccountReference account,
        DateTimeOffset expiresAtUtc)
    {
        ProviderCapabilitySnapshot snapshot = new(
            account,
            Now.AddMinutes(-5),
            "google:test-current",
            [new ProviderCapability(
                GoogleGenerationProvider.SynthesizeOperationStableId,
                ProviderCapabilityState.Supported,
                ProviderLifecycleState.Available)]);
        return new StoredProviderCapabilitySnapshot(Guid.NewGuid(), snapshot, expiresAtUtc);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NeverResolvingCredentialResolver : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Credential resolution must not occur while materializing production transport evidence.");
    }
}
