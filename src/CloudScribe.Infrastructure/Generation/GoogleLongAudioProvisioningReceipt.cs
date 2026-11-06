namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleLongAudioProvisioningReceipt(int Version, Guid TransactionId, string PlanSha256, string Phase);
