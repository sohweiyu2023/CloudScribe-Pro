using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Potentially sensitive, bounded redacted response evidence; never routine logging or export data.</summary>
public sealed record PrivateProviderResponseEnvelope(int Version, int HttpStatus, int ObservedBytes,
    bool ReadCompleted, string ReadState, string ParseState, string RedactedBody, bool Truncated, bool DetailsOmitted,
    bool RedactionApplied, GoogleErrorStatus ErrorStatus)
{
    public override string ToString() => "Private response envelope (contents omitted)";
}
