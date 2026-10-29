namespace CloudScribe.Application.Generation;

/// <summary>A pin of a retained historical attempt. Mapping does not clear, refund, renew or authorize it.</summary>
public sealed record ChirpHistoricalAttempt(string IdempotencyKey, string StateSha256, long RetainedReservationMinorUnits);
