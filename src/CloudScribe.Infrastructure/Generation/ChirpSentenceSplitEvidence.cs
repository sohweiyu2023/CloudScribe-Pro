using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Inputs require independent transaction-owned authentication and confer no authority.</summary>
public sealed record ChirpSentenceSplitEvidence(Guid AuthorizationId, Guid RootAuthorizationId,
    string PayloadSha256, int PayloadBytes, string AttemptRowSha256, string QueueRowSha256,
    int HttpStatus, GoogleErrorStatus ErrorStatus, GoogleValidationMessageHint MessageHint,
    bool Consumed, bool SubmissionAttempted, bool Unresolved, bool HasPendingWork, bool HasAcceptedOrPendingOutput);
