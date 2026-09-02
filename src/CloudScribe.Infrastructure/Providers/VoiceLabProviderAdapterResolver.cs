using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Providers;

public sealed class VoiceLabProviderAdapterResolver
{
    private readonly IProviderFactoryRegistry _registry;

    public VoiceLabProviderAdapterResolver(IProviderFactoryRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async ValueTask<IVoiceLabProviderAdapter> ResolveAsync(
        string providerStableId,
        string accountStableId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(providerStableId, nameof(providerStableId));
        ValidateIdentity(accountStableId, nameof(accountStableId));

        if (!_registry.TryGetFactory(providerStableId, out IProviderAdapterFactory? factory) || factory is null)
            throw new InvalidOperationException("Voice Lab provider factory is unavailable.");

        if (!string.Equals(factory.Descriptor.StableId, providerStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Voice Lab provider factory identity mismatch.");

        IProviderAdapter adapter = await factory.CreateAdapterAsync(accountStableId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(adapter.Descriptor.StableId, providerStableId, StringComparison.Ordinal))
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Voice Lab provider adapter identity mismatch.");
        }

        if (adapter is not IVoiceLabProviderAdapter voiceLabAdapter)
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Provider adapter does not expose Voice Lab capability.");
        }

        return voiceLabAdapter;
    }

    private static void ValidateIdentity(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            throw new InvalidOperationException($"Voice Lab identity '{parameterName}' must be canonical.");
    }
}
