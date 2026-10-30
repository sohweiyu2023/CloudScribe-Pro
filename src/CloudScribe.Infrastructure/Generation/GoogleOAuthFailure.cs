namespace CloudScribe.Infrastructure.Generation;

public enum GoogleOAuthFailure
{
    None, Unrecognized, Malformed, TooLarge, Unreadable, TimedOut,
    InvalidGrant, InvalidClient, UnauthorizedClient, InvalidScope, InvalidRequest, AccessDenied,
    TemporarilyUnavailable, ServerError,
}
