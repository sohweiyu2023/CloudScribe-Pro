using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Configuration;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only authority within the existing installer/binary trust boundary, not an OS privilege boundary.
/// No runtime method can admit a policy or change its independently reviewed installer pin.</summary>
public sealed partial class InstalledRecoveryAdmissionStore(string applicationDirectory, AppPaths paths, TimeProvider clock)
    : IReviewedHistoricalAudioAnchorStore, IGoogleRecoveryPredecessorEvidenceResolver
{
    private static readonly JsonSerializerOptions PolicyJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<ReviewedHistoricalAudioAnchor?> LoadAdmittedAsync(string evidenceId, CancellationToken cancellationToken = default)
    {
        var policy = await LoadVerifiedPolicyAsync(cancellationToken).ConfigureAwait(false);
        var matches = policy?.HistoricalAudio.Where(x => string.Equals(x.EvidenceId, evidenceId, StringComparison.Ordinal)).ToArray();
        if (matches is not { Length: 1 }) return null;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(matches[0].Anchor);
        return new(evidenceId, bytes, Hash(bytes), policy!.ExpiresAtUtc);
    }

    public async Task<GoogleRecoveryPredecessorEvidence?> ResolveAsync(string accountId, string operationId, string historicalKey,
        string exactHistoricalStateSha256, CancellationToken cancellationToken = default)
    {
        var policy = await LoadVerifiedPolicyAsync(cancellationToken).ConfigureAwait(false);
        var matches = policy?.Predecessors.Where(x => string.Equals(x.AccountId, accountId, StringComparison.Ordinal)
            && string.Equals(x.OperationId, operationId, StringComparison.Ordinal) && string.Equals(x.HistoricalKey, historicalKey, StringComparison.Ordinal)
            && string.Equals(x.HistoricalStateSha256, exactHistoricalStateSha256, StringComparison.Ordinal)).ToArray();
        if (matches is not { Length: 1 } || matches[0].ExpiresAtUtc <= clock.GetUtcNow()) return null;
        // A canonical successor digest binds its exact expiry; schema3 validation already requires it within policy expiry.
        if (matches[0].Successor is not null) return matches[0];
        return matches[0] with { ExpiresAtUtc = matches[0].ExpiresAtUtc < policy!.ExpiresAtUtc ? matches[0].ExpiresAtUtc : policy.ExpiresAtUtc };
    }

    public async Task<IReadOnlyList<string>> ListHistoricalEvidenceIdsAsync(CancellationToken cancellationToken = default) =>
        (await LoadVerifiedPolicyAsync(cancellationToken).ConfigureAwait(false))?.HistoricalAudio.Select(x => x.EvidenceId).ToArray() ?? [];

    public async Task<RecoveryAdmissionPolicy?> LoadVerifiedPolicyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
            string install = Path.GetDirectoryName(app) ?? throw new InvalidDataException("Install root is missing.");
            byte[] markerBytes = await ReadBoundedAsync(Path.Combine(install, "cloudscribe-install.json"), 16384, cancellationToken).ConfigureAwait(false);
            using var marker = JsonDocument.Parse(markerBytes);
            RequireUniqueProperties(marker.RootElement);
            if (!string.Equals(marker.RootElement.GetProperty("Product").GetString(), "CloudScribe Pro", StringComparison.Ordinal)) return null;
            string? expected = marker.RootElement.GetProperty("RecoveryPolicySha256").GetString();
            if (!IsHash(expected)) return null;
            byte[] policyBytes = await ReadBoundedAsync(Path.Combine(install, "recovery-admission-policy.json"), 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Hash(policyBytes), expected, StringComparison.OrdinalIgnoreCase)) return null;
            using var document = JsonDocument.Parse(policyBytes, new JsonDocumentOptions { MaxDepth = 16 });
            RequireUniqueProperties(document.RootElement);
            var policy = JsonSerializer.Deserialize<RecoveryAdmissionPolicy>(policyBytes, PolicyJson);
            if (policy is null || !ValidPolicy(policy)) return null;
            string actualAppSha256 = await HashFileAsync(Path.Combine(app, "CloudScribe.dll"), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(marker.RootElement.GetProperty("AppSha256").GetString(), actualAppSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(policy.AppSha256, actualAppSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(policy.InfrastructureSha256, await HashFileAsync(Path.Combine(app, "CloudScribe.Infrastructure.dll"), cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(policy.ProfileRootSha256, ProfileHash(paths.RootDirectory), StringComparison.Ordinal)) return null;
            return policy;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or NullReferenceException)
        { return null; } // Safe absence; never log policy bytes, paths or raw exception data.
    }

    public static string ProfileHash(string profile) => Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()));
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
}
