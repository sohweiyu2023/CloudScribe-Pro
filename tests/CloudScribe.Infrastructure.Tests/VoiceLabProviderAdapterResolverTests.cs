using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

#pragma warning disable MA0004 // xUnit1030 requires test awaits to preserve the test synchronization context.

public sealed class VoiceLabProviderAdapterResolverTests
{
    [Fact]
    public async Task ResolveAsyncRejectsMissingProviderFactory()
    {
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry([]));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(
                "google",
                "account-1",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsyncRejectsNonCanonicalProviderIdentityBeforeFactoryLookup()
    {
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry([]));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(
                " google",
                "account-1",
                TestContext.Current.CancellationToken));

        Assert.Contains("providerStableId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsyncRejectsNonCanonicalAccountIdentityBeforeAdapterCreation()
    {
        VoiceLabAdapter adapter = new("google");
        FakeFactory factory = new("google", adapter);
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry([factory]));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(
                "google",
                "account-1\n",
                TestContext.Current.CancellationToken));

        Assert.Contains("accountStableId", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, factory.CreateCalls);
        Assert.False(adapter.Disposed);
    }

    [Fact]
    public async Task ResolveAsyncRejectsNullAdapterFailClosed()
    {
        NullAdapterFactory factory = new("google");
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry([factory]));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(
                "google",
                "account-1",
                TestContext.Current.CancellationToken));

        Assert.Contains("returned no adapter", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public async Task ResolveAsyncRejectsGenericAdapterAndDisposesIt()
    {
        GenericAdapter adapter = new("google");
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry(
            [new FakeFactory("google", adapter)]));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(
                "google",
                "account-1",
                TestContext.Current.CancellationToken));

        Assert.True(adapter.Disposed);
    }

    [Fact]
    public async Task ResolveAsyncReturnsExactVoiceLabCapability()
    {
        VoiceLabAdapter adapter = new("google");
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry(
            [new FakeFactory("google", adapter)]));

        IVoiceLabProviderAdapter resolved = await resolver.ResolveAsync(
            "google",
            "account-1",
            TestContext.Current.CancellationToken);

        Assert.Same(adapter, resolved);
        Assert.False(adapter.Disposed);
        await resolved.DisposeAsync();
    }

    private sealed class FakeFactory(string providerId, IProviderAdapter adapter) : IProviderAdapterFactory
    {
        public ProviderDescriptor Descriptor { get; } = new(providerId, providerId, true, true);
        public int CreateCalls { get; private set; }

        public ValueTask<IProviderAdapter> CreateAdapterAsync(
            string accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            Assert.Equal("account-1", accountId);
            return ValueTask.FromResult(adapter);
        }
    }

    private sealed class NullAdapterFactory(string providerId) : IProviderAdapterFactory
    {
        public ProviderDescriptor Descriptor { get; } = new(providerId, providerId, true, true);
        public int CreateCalls { get; private set; }

        public ValueTask<IProviderAdapter> CreateAdapterAsync(
            string accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            Assert.Equal("account-1", accountId);
            return ValueTask.FromResult<IProviderAdapter>(null!);
        }
    }

    private class GenericAdapter(string providerId) : IProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new(providerId, providerId, true, true);
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class VoiceLabAdapter(string providerId) : GenericAdapter(providerId), IVoiceLabProviderAdapter
    {
    }
}

#pragma warning restore MA0004
