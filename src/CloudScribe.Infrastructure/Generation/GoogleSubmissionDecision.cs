namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleSubmissionDecision(
    GoogleSubmissionDisposition Disposition,
    TimeSpan? Delay,
    string Reason);
