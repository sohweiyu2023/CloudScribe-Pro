using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Trusted opt-in protected-retention boundary for a true exact Long Audio request, never paid authority.</summary>
public interface IGoogleLongAudioResponseObserver
{
    bool IsReadyFor(GoogleLongAudioRequest request);
    Guid Begin(GoogleLongAudioRequest request);
    bool IsActiveFor(Guid ticket, GoogleLongAudioRequest request);
    Task<GoogleLongAudioObservation> ObserveAsync(Guid ticket, GoogleLongAudioRequest request, int httpStatus,
        ReadOnlyMemory<byte> boundedBody, GoogleResponseReadState readState, string transientAccessToken, bool operationError);
    /// <summary>Bounded GCS metadata only, including HTTP-200 local parse failures. Never media bytes or an invented provider error.</summary>
    Task<GoogleLongAudioObservation> ObserveStorageMetadataAsync(Guid ticket, GoogleLongAudioRequest request, int httpStatus,
        ReadOnlyMemory<byte> boundedBody, GoogleResponseReadState readState, string transientAccessToken) =>
        ObserveAsync(ticket, request, httpStatus, boundedBody, readState, transientAccessToken, false);
    void FinishResponse(Guid ticket);
}
