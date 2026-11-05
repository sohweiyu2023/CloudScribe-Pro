using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Safe retention evidence only; never raw provider text, paid permission or accepted media.</summary>
public sealed record GoogleLongAudioObservation(Guid? PrivateRecordId, GoogleResponseReadState ReadState,
    GoogleLongAudioParseState ParseState, GoogleErrorStatus ErrorStatus, int ObservedBytes,
    GoogleExplanationRetentionState Retention, bool ReadbackVerified, bool Truncated, bool DetailsOmitted);
