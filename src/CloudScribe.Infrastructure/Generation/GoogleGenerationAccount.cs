namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationAccount(
    string AccountId,
    string CredentialReferenceId,
    Uri Endpoint,
    string Region)
{
    public GoogleGenerationAccount Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CredentialReferenceId);
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(Region);
        if (!Endpoint.IsAbsoluteUri || !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Google generation endpoint must be an absolute HTTPS URI.", nameof(Endpoint));
        }
        if (!string.IsNullOrEmpty(Endpoint.UserInfo))
        {
            throw new ArgumentException("Provider endpoints must not embed credentials.", nameof(Endpoint));
        }
        if (!string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment))
        {
            throw new ArgumentException("Provider endpoint identity must not contain query or fragment data.", nameof(Endpoint));
        }
        return this;
    }
}
