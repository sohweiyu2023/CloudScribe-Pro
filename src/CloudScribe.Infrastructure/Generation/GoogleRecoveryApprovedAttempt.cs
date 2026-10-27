namespace CloudScribe.Infrastructure.Generation;

/// <summary>Durably reserved identity; still requires fresh production admission and owned claim.</summary>
public sealed record GoogleRecoveryApprovedAttempt(Guid AuthorizationId, string IdempotencyKey,
    string ReviewSha256, long ReservedMinorUnits);
