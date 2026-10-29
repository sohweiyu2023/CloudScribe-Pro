using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

/// <summary>Fixed informational UI text only. Does not change retry, approval or financial policy.</summary>
public static class GoogleValidationGuidance
{
    public static string For(string? diagnosticCode, GoogleErrorStatus status, GoogleValidationMessageHint hint) =>
        string.Equals(diagnosticCode, "google-http-400", StringComparison.Ordinal)
        && status == GoogleErrorStatus.InvalidArgument && hint == GoogleValidationMessageHint.SentenceLengthReported
            ? " Google reported sentence length. Review a shorter request using existing sentence or clause boundaries; preserve the source and previous holds. A changed request needs fresh exact approval; do not retry automatically."
            : string.Empty;
}
