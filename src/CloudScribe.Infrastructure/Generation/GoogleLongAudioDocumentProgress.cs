using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleLongAudioDocumentProgress(LongAudioWorkflowStage Stage, GoogleLongAudioCheckpoint? Checkpoint,
    int? VerifiedRanges = null, int? VerifiedScalars = null, int? TotalScalars = null);
