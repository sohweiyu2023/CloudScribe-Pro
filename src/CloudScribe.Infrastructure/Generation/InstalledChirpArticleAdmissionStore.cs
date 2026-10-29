using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only installer pin within the existing binary/profile trust boundary. Never admits runtime input.</summary>
public sealed class InstalledChirpArticleAdmissionStore(string applicationDirectory, AppPaths paths, TimeProvider clock)
    : IChirpArticleAdmissionStore
{
    public async Task<ChirpArticleAdmissionPolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
            string install = Path.GetDirectoryName(app) ?? throw new InvalidDataException("Installation identity is absent.");
            byte[] markerBytes = await ChirpPolicyFiles.ReadAsync(Path.Combine(install, "cloudscribe-install.json"), 16384, cancellationToken).ConfigureAwait(false);
            using var marker = JsonDocument.Parse(markerBytes);
            ChirpPolicyFiles.RequireUnique(marker.RootElement);
            if (!string.Equals(marker.RootElement.GetProperty("Product").GetString(), "CloudScribe Pro", StringComparison.Ordinal)
                || !marker.RootElement.TryGetProperty("ChirpArticlePolicySha256", out var pin) || !ChirpPolicyValidation.IsHash(pin.GetString())) return null;
            byte[] bytes = await ChirpPolicyFiles.ReadAsync(Path.Combine(install, "chirp-article-admission-policy.json"), 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(pin.GetString(), ChirpPolicyFiles.Hash(bytes), StringComparison.Ordinal)) return null;
            var policy = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(bytes);
            ChirpPolicyValidation.RequireValid(policy, clock.GetUtcNow());
            string appHash = ChirpPolicyFiles.Hash(await ChirpPolicyFiles.ReadAsync(Path.Combine(app, "CloudScribe.dll"), 512 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
            string infrastructureHash = ChirpPolicyFiles.Hash(await ChirpPolicyFiles.ReadAsync(Path.Combine(app, "CloudScribe.Infrastructure.dll"), 512 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(policy.AppSha256, appHash, StringComparison.Ordinal)
                || !string.Equals(marker.RootElement.GetProperty("AppSha256").GetString(), appHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(policy.InfrastructureSha256, infrastructureHash, StringComparison.Ordinal)
                || !string.Equals(policy.ProfileRootSha256, InstalledRecoveryAdmissionStore.ProfileHash(paths.RootDirectory), StringComparison.Ordinal)) return null;
            return policy;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or NullReferenceException or OverflowException)
        { return null; } // No raw policy, private path or exception text reaches logs.
    }
}
