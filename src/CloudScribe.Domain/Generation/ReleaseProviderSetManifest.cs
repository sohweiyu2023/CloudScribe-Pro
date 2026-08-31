using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Domain.Generation;

public sealed class ReleaseProviderSetManifest
{
    public ReleaseProviderSetManifest(
        string sourceBundleSha256,
        string sourceMemberPath,
        IEnumerable<ReleaseProviderDescriptor> providers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBundleSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMemberPath);
        ArgumentNullException.ThrowIfNull(providers);
        RequireSha256(sourceBundleSha256, nameof(sourceBundleSha256));

        var items = providers.OrderBy(static p => p.ProviderStableId, StringComparer.Ordinal).ToArray();
        if (items.Length == 0) throw new ArgumentException("At least one authenticated release provider is required.", nameof(providers));
        if (items.Select(static p => p.ProviderStableId).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Release provider stable identities must be unique.", nameof(providers));

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProviderStableId))
                throw new ArgumentException("Release provider stable identity is required.", nameof(providers));
            if (string.IsNullOrWhiteSpace(item.DisplayName))
                throw new ArgumentException("Release provider display name is required.", nameof(providers));
            RequireSha256(item.ControlMemberSha256, nameof(providers));
            if (item.OperationStableIds is null)
                throw new ArgumentException("Release provider operation identities are required.", nameof(providers));
            if (item.OperationStableIds.Count == 0 || item.OperationStableIds.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Every release provider requires at least one stable operation identity.", nameof(providers));
        }

        SourceBundleSha256 = sourceBundleSha256.ToLowerInvariant();
        SourceMemberPath = sourceMemberPath;
        Providers = items;
        ManifestSha256 = ComputeManifestSha256();
    }

    public string SourceBundleSha256 { get; }
    public string SourceMemberPath { get; }
    public IReadOnlyList<ReleaseProviderDescriptor> Providers { get; }
    public string ManifestSha256 { get; }

    public ReleaseProviderDescriptor RequireProvider(string providerStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        return Providers.SingleOrDefault(p => string.Equals(p.ProviderStableId, providerStableId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Provider is not admitted by the authenticated release-provider manifest.");
    }

    public ReleaseProviderDescriptor RequireProviderOperation(string providerStableId, string operationStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationStableId);
        var provider = RequireProvider(providerStableId);
        if (!provider.OperationStableIds.Contains(operationStableId))
        {
            throw new InvalidOperationException("Provider operation is not admitted by the authenticated release-provider manifest.");
        }

        return provider;
    }

    private string ComputeManifestSha256()
    {
        var builder = new StringBuilder();
        builder.Append("cloudscribe-release-provider-set-v1\n");
        builder.Append(SourceBundleSha256).Append('\n').Append(SourceMemberPath).Append('\n');
        foreach (var provider in Providers)
        {
            builder.Append(provider.ProviderStableId).Append('|')
                .Append(provider.DisplayName).Append('|')
                .Append(provider.ControlMemberSha256.ToLowerInvariant()).Append('|');
            foreach (var operation in provider.OperationStableIds.Order(StringComparer.Ordinal))
                builder.Append(operation).Append(',');
            builder.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected a 64-character SHA-256 hexadecimal digest.", parameterName);
    }
}
