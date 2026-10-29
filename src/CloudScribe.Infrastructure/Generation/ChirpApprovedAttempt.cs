namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpApprovedAttempt(Guid AuthorizationId, string IdempotencyKey, long ReservedMinorUnits);
