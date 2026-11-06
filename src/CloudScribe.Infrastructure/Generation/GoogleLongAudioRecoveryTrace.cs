using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Safe tree evidence only: no text, credentials, file paths or provider prose.</summary>
public sealed record GoogleLongAudioRecoveryTrace(Guid RootAttemptId, Guid AttemptId, Guid? ParentAttemptId,
    int StartScalar, int EndScalarExclusive, int SplitDepth, string PayloadSha256, LongAudioRecoveryStage Stage, GoogleLongAudioFailureKind? FailureKind);
