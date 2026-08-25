namespace CloudScribe.Domain.Generation;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    MissingMedia,
    CorruptMedia,
}
