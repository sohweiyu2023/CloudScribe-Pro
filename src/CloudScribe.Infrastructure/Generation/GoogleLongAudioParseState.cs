namespace CloudScribe.Infrastructure.Generation;

public enum GoogleLongAudioParseState
{
    Unavailable, Json, Empty, NonJsonOrMalformed, Excluded, InvalidUtf8, RedactionUnavailable, SuccessBodyExcluded,
}
