namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationAccount(
    string AccountId,
    string CredentialReferenceId,
    Uri Endpoint,
    string Region)
{
    public GoogleGenerationAccount Validate()
    {
        ValidateIdentity(AccountId, CredentialReferenceId, Endpoint, Region);
        return this;
    }

    private static void ValidateIdentity(string accountId, string credentialReferenceId, Uri endpoint, string region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReferenceId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        if (!endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Google generation endpoint must be an absolute HTTPS URI.", nameof(endpoint));
        }
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Provider endpoints must not embed credentials.", nameof(endpoint));
        }
        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("Provider endpoint identity must not contain query or fragment data.", nameof(endpoint));
        }
    }
}
