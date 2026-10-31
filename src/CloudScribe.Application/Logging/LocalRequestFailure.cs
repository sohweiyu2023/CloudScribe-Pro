namespace CloudScribe.Application.Logging;

public sealed record LocalRequestFailure(LocalRequestReason Reason, long ReviewAgeMilliseconds,
    DateTimeOffset? ReviewExpiresUtc, DateTimeOffset? AdmissionExpiresUtc, bool EvidenceComplete);
