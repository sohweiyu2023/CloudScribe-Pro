using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleGenerationProductionEvidenceResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly Uri EndpointOrigin = new("https://speech.example.test/", UriKind.Absolute);

    [Fact]
    public async Task ResolveAsyncReturnsOnlyCurrentMatchingUsableEvidence()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, Now.AddHours(1));
        var resolver = new GoogleGenerationProductionEvidenceResolver(
            new AccountStore(account),
            new CapabilityStore(capability),
            new FixedTimeProvider(Now));

        GoogleGenerationProductionEvidence evidence = await resolver.ResolveAsync(
            account.Reference.AccountId,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Same(account, evidence.Account);
        Assert.Same(capability, evidence.Capability);
    }

    [Fact]
    public async Task ResolveAsyncRejectsDisabledAccount()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: false);
        var resolver = CreateResolver(account, CreateCapability(account.Reference, Now.AddHours(1)));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            account.Reference.AccountId,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsyncRejectsStaleCapabilityEvidence()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true);
        var resolver = CreateResolver(account, CreateCapability(account.Reference, Now));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            account.Reference.AccountId,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsyncRejectsCurrentAccountWithoutAdmittedEndpointOrigin()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true, includeEndpointOrigin: false);
        var resolver = CreateResolver(account, CreateCapability(account.Reference, Now.AddHours(1)));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            account.Reference.AccountId,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("no admitted endpoint origin", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsyncRejectsCapabilityCapturedWithoutEndpointOrigin()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true);
        ProviderAccountReference capturedWithoutOrigin = new(
            GoogleGenerationProvider.StableProviderId,
            account.Reference.AccountId,
            "Google primary",
            new CredentialReference("google.primary"),
            "google-tts-v1",
            "global");
        var resolver = CreateResolver(account, CreateCapability(capturedWithoutOrigin, Now.AddHours(1)));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            account.Reference.AccountId,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("no captured endpoint origin", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsyncRejectsCapabilityCapturedForPreviousEndpointOrigin()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true);
        ProviderAccountReference oldBinding = new(
            GoogleGenerationProvider.StableProviderId,
            account.Reference.AccountId,
            "Google primary",
            new CredentialReference("google.primary"),
            "google-tts-v1",
            "global",
            new Uri("https://old-speech.example.test/", UriKind.Absolute));
        var resolver = CreateResolver(account, CreateCapability(oldBinding, Now.AddHours(1)));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            account.Reference.AccountId,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("changed after capability evidence", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsyncRejectsCapabilityCapturedForPreviousAccountBinding()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true);
        ProviderAccountReference oldBinding = new(
            GoogleGenerationProvider.StableProviderId,
            account.Reference.AccountId,
            "Google primary",
            new CredentialReference("google.primary.old"),
            "google-tts-v1",
            "global",
            EndpointOrigin);
        var resolver = CreateResolver(account, CreateCapability(oldBinding, Now.AddHours(1)));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            account.Reference.AccountId,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("changed after capability evidence", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsyncRejectsNonUsableSynthesisCapability()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(
            account.Reference,
            Now.AddHours(1),
            ProviderCapabilityState.Unsupported,
            ProviderLifecycleState.Available,
            "Synthesis unavailable for this account.");
        var resolver = CreateResolver(account, capability);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            account.Reference.AccountId,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("not usable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GoogleGenerationProductionEvidenceResolver CreateResolver(
        ProviderAccountSnapshot account,
        StoredProviderCapabilitySnapshot capability) => new(
        new AccountStore(account),
        new CapabilityStore(capability),
        new FixedTimeProvider(Now));

    private static ProviderAccountSnapshot CreateAccount(bool isEnabled, bool includeEndpointOrigin = true)
    {
        ProviderAccountReference reference = new(
            GoogleGenerationProvider.StableProviderId,
            "primary",
            "Google primary",
            new CredentialReference("google.primary"),
            "google-tts-v1",
            "global",
            includeEndpointOrigin ? EndpointOrigin : null);
        return new ProviderAccountSnapshot(reference, isEnabled, 1, Now.AddDays(-1), Now.AddHours(-1));
    }

    private static StoredProviderCapabilitySnapshot CreateCapability(
        ProviderAccountReference account,
        DateTimeOffset expiresAtUtc,
        ProviderCapabilityState state = ProviderCapabilityState.Supported,
        ProviderLifecycleState lifecycle = ProviderLifecycleState.Available,
        string? disabledReason = null)
    {
        ProviderCapabilitySnapshot snapshot = new(
            account,
            Now.AddMinutes(-5),
            "google:test-current",
            [new ProviderCapability(GoogleGenerationProvider.SynthesizeOperationStableId, state, lifecycle, disabledReason)]);
        return new StoredProviderCapabilitySnapshot(Guid.NewGuid(), snapshot, expiresAtUtc);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AccountStore(ProviderAccountSnapshot? account) : IProviderAccountStore
    {
        public Task<ProviderAccountSnapshot> CreateAsync(ProviderAccountReference accountReference, bool isEnabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccountSnapshot> UpdateAsync(ProviderAccountReference accountReference, bool isEnabled, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccountSnapshot?> FindAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(account is not null &&
                string.Equals(account.Reference.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(account.Reference.AccountId, accountId, StringComparison.Ordinal)
                ? account
                : null);
        }

        public Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccountSnapshot>>(account is null ? [] : [account]);
    }

    private sealed class CapabilityStore(StoredProviderCapabilitySnapshot? capability) : IProviderCapabilitySnapshotStore
    {
        public Task<StoredProviderCapabilitySnapshot> SaveAsync(ProviderCapabilitySnapshot snapshot, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredProviderCapabilitySnapshot?> GetLatestAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(capability is not null &&
                string.Equals(capability.Snapshot.Account.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(capability.Snapshot.Account.AccountId, accountId, StringComparison.Ordinal)
                ? capability
                : null);
        }

        public Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(string providerStableId, string accountId, int maximumCount = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredProviderCapabilitySnapshot>>(capability is null ? [] : [capability]);
    }
}
