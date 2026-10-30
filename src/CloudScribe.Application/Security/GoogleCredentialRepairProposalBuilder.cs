using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Security;

/// <summary>Validates an offline proposal only. No vault, database, JSON, logging or provider dependency exists.</summary>
public static class GoogleCredentialRepairProposalBuilder
{
    public static GoogleCredentialRepairProposal Prepare(
        GoogleCredentialRepairScope expected,
        GoogleCredentialRepairScope current,
        GoogleCredentialRepairPresence presence,
        string candidateIdentitySha256,
        Guid repairId)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(presence);
        ValidateScope(expected);
        ValidateScope(current);
        RequireDigest(candidateIdentitySha256);
        if (repairId == Guid.Empty)
            throw new ArgumentException("A fresh repair identity is required.", nameof(repairId));
        if (expected != current)
            throw new InvalidOperationException("The observed account or repair scope changed; prepare again.");
        if (!string.Equals(expected.OriginalIdentityAnchorSha256, candidateIdentitySha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The candidate identity does not match the independently established original identity anchor.");
        if (presence.Marker != CredentialRolePresence.Absent || presence.Metadata != CredentialRolePresence.Absent
            || presence.PrivateKey != CredentialRolePresence.Absent)
            throw new InvalidOperationException("Every existing credential role must be confirmed absent; unknown or present material cannot be overwritten.");
        string proposedReference = new CredentialReference($"google-repair.{repairId:N}").TargetName;
        if (string.Equals(proposedReference, expected.ExistingCredentialReference, StringComparison.Ordinal))
            throw new InvalidOperationException("Repair staging must use a distinct credential generation.");
        return new(repairId, expected, proposedReference, candidateIdentitySha256);
    }

    private static void ValidateScope(GoogleCredentialRepairScope scope)
    {
        RequireCanonicalId(scope.AccountId, 64);
        RequireCanonicalId(scope.ProjectId, 64);
        RequireCanonicalId(scope.ExistingCredentialReference, 192);
        ArgumentOutOfRangeException.ThrowIfLessThan(scope.AccountRevision, 1, nameof(scope));
        RequireDigest(scope.FullAccountRowSha256);
        RequireDigest(scope.EndpointBindingSha256);
        RequireDigest(scope.ProfileSha256);
        RequireDigest(scope.BuildSha256);
        RequireDigest(scope.OriginalIdentityAnchorSha256);
    }

    private static void RequireCanonicalId(string value, int maximumLength)
    {
        string normalized = new CredentialReference(value).TargetName;
        if (value.Length > maximumLength || !string.Equals(value, normalized, StringComparison.Ordinal))
            throw new ArgumentException("A canonical bounded account, project or credential identity is required.", nameof(value));
    }

    private static void RequireDigest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("A canonical independently established SHA256 binding is required.");
    }
}
