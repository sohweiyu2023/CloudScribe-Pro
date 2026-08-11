using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ProviderFactoryRegistryTests
{
    [Fact]
    public void EmptyStartupRegistryDoesNotConstructProviders()
    {
        ProviderFactoryRegistry registry = new([]);

        Assert.Empty(registry.AvailableProviders);
        Assert.False(registry.TryGetFactory("google", out _));
    }

    [Fact]
    public void DescriptorNormalizesUserFacingWhitespaceButRequiresCanonicalStableId()
    {
        ProviderDescriptor descriptor = new("google-cloud", "  Google Cloud  ", true, true);

        Assert.Equal("google-cloud", descriptor.StableId);
        Assert.Equal("Google Cloud", descriptor.DisplayName);
        Assert.Throws<ArgumentException>(() => new ProviderDescriptor("Google", "Google", true, true));
        Assert.Throws<ArgumentException>(() => new ProviderDescriptor("-google", "Google", true, true));
        Assert.Throws<ArgumentException>(() => new ProviderDescriptor("google", "Google\nCloud", true, true));
        Assert.Throws<ArgumentException>(() => new ProviderDescriptor("google", "Google\u202eCloud", true, true));
    }

    [Fact]
    public void RegistrySnapshotsEachValidatedDescriptorExactlyOnce()
    {
        SingleReadDescriptorFactory factory = new(new ProviderDescriptor("google", "Google", true, true));

        ProviderFactoryRegistry registry = new([factory]);

        Assert.Single(registry.AvailableProviders);
        Assert.Equal(1, factory.DescriptorReadCount);
    }

    [Fact]
    public void RegistryRejectsDuplicateAndNullDescriptors()
    {
        ProviderDescriptor descriptor = new("google", "Google", true, true);

        Assert.Throws<InvalidOperationException>(() => new ProviderFactoryRegistry(
            [new FakeFactory(descriptor), new FakeFactory(descriptor)]));
        Assert.Throws<InvalidOperationException>(() => new ProviderFactoryRegistry(
            [new NullDescriptorFactory()]));
    }

    private sealed class FakeFactory(ProviderDescriptor descriptor) : IProviderAdapterFactory
    {
        public ProviderDescriptor Descriptor { get; } = descriptor;

        public ValueTask<IProviderAdapter> CreateAdapterAsync(
            string accountId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SingleReadDescriptorFactory(ProviderDescriptor descriptor) : IProviderAdapterFactory
    {
        public int DescriptorReadCount { get; private set; }

        public ProviderDescriptor Descriptor
        {
            get
            {
                DescriptorReadCount++;
                if (DescriptorReadCount > 1)
                {
                    throw new InvalidOperationException("Descriptor was read more than once.");
                }

                return descriptor;
            }
        }

        public ValueTask<IProviderAdapter> CreateAdapterAsync(
            string accountId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NullDescriptorFactory : IProviderAdapterFactory
    {
        public ProviderDescriptor Descriptor => null!;

        public ValueTask<IProviderAdapter> CreateAdapterAsync(
            string accountId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
