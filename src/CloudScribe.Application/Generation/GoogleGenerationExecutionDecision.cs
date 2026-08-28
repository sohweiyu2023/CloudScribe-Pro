namespace CloudScribe.Application.Generation;

public sealed record GoogleGenerationExecutionDecision(bool MayQueue, bool MaySubmit, string Reason);
