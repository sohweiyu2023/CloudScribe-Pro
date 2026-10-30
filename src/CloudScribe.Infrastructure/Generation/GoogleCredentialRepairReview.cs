namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleCredentialRepairReview(Guid ReviewId, Guid RepairId, string ReviewSha256, string AccountId,
    GoogleCredentialRepairIdentity Identity, string OldCredentialReference, string NewCredentialReference,
    DateTimeOffset ExpiresAtUtc, bool Resuming, string ProfileSha256, string BuildSha256);
