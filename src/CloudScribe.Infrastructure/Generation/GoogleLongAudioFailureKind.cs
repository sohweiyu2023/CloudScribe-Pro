namespace CloudScribe.Infrastructure.Generation;

/// <summary>Safe provider classification, never admission or permission to retry.</summary>
public enum GoogleLongAudioFailureKind
{
    Unknown = 0,
    SentenceLengthReported = 1,
    OtherInputRejected = 2,
    AuthenticationOrPermission = 3,
    QuotaOrRateLimited = 4,
    TransientOrTimeout = 5,
}
