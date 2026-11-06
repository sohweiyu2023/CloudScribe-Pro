namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleLongAudioContinuationReview(GoogleLongAudioDocumentReview Review, GoogleLongAudioContinuationKind Kind,
    GoogleLongAudioCheckpoint? Checkpoint = null);
