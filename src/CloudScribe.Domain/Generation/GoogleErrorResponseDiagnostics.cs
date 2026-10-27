namespace CloudScribe.Domain.Generation;

/// <summary>Bounded metadata only. Contains no free-form provider values or body-derived hashes.</summary>
public sealed record GoogleErrorResponseDiagnostics(GoogleErrorBodyParseState Parse, GoogleErrorMemberState Status,
    GoogleErrorMemberState Message, int MessageUtf16Characters, GoogleErrorDetailsState Details, int DetailCount,
    GoogleValidationMessageHint MessageHint);
