using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabProviderAdapterResolverTests
{
    [Fact]
    public async Task ResolveAsyncRejectsMissingProviderFactory()
    {
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry([]));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync("google", "account-1"));
    }

    [Fact]
    public async Task ResolveAsyncRejectsGenericAdapterAndDisposesIt()
    {
        GenericAdapter adapter = new("google");
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry(
            [new FakeFactory("google", adapter)]));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync("google", "account-1"));

        Assert.True(adapter.Disposed);
    }

    [Fact]
    public async Task ResolveAsyncReturnsExactVoiceLabCapability()
    {
        VoiceLabAdapter adapter = new("google");
        VoiceLabProviderAdapterResolver resolver = new(new ProviderFactoryRegistry(
            [new FakeFactory("google", adapter)]));

        IVoiceLabProviderAdapter resolved = await resolver.ResolveAsync("google", "account-1");

        Assert.Same(adapter, resolved);
        Assert.False(adapter.Disposed);
        await resolved.DisposeAsync();
    }

    private sealed class FakeFactory(string providerId, IProviderAdapter adapter) : IProviderAdapterFactory
    {
        public ProviderDescriptor Descriptor { get; } = new(providerId, providerId, true, true);

        public ValueTask<IProviderAdapter> CreateAdapterAsync(
            string accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("account-1", accountId);
            return ValueTask.FromResult(adapter);
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
