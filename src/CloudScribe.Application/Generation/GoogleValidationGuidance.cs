using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

/// <summary>Fixed informational UI text only. Does not change retry, approval or financial policy.</summary>
public static class GoogleValidationGuidance
{
    public static string For(string? diagnosticCode, GoogleErrorStatus status, GoogleValidationMessageHint hint)
    {
        if (!string.Equals(diagnosticCode, "google-http-400", StringComparison.Ordinal) || status != GoogleErrorStatus.InvalidArgument)
            return string.Empty;
        return hint switch
        {
            GoogleValidationMessageHint.SentenceLengthReported => " Google reported sentence length. Review a shorter request using existing sentence or clause boundaries; preserve the source and previous holds. A changed request needs fresh exact approval; do not retry automatically.",
            GoogleValidationMessageHint.Unrecognized => " The provider explanation was not recognized by the safe classifier. Ordinary logs exclude its wording. If one-response private capture was enabled for THIS exact request, inspect its private record; an older record is not evidence for this attempt. Otherwise the discarded explanation cannot be recovered. Any future diagnostic request needs separate exact approval and explicit private capture after reservation, before Generate. Do not retry automatically; retain all holds.",
            _ => string.Empty,
        };
    }
}
