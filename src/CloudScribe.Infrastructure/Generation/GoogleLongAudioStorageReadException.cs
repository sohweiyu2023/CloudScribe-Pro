namespace CloudScribe.Infrastructure.Generation;

/// <summary>Actual storage HTTP outcome with safe protected-retention status, never raw body or credential material.</summary>
public sealed class GoogleLongAudioStorageReadException(int httpStatus, GoogleLongAudioObservation observation, Exception? innerException = null)
    : InvalidOperationException("Private output read failed; inspect its actual HTTP status and protected-retention state.", innerException)
{
    public int HttpStatus { get; } = httpStatus;
    public GoogleLongAudioObservation Observation { get; } = observation;
}
