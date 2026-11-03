namespace CloudScribe.Infrastructure.Generation;

internal sealed record PrivateProviderExplanationText(string Message, bool DetailsOmitted, bool Truncated = false)
{
    public override string ToString() => "Private redacted provider explanation (contents omitted)";
}
