namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationProjectAuthorizationEvidence(
    string AccountId,
    string ProjectId,
    string ModelId,
    string CredentialReferenceId,
    string CapabilityProvenanceId,
    string EndpointId,
    string RegionId,
    string EndpointOrigin,
    bool Authorized,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public GoogleGenerationProjectAuthorizationEvidence Validate(DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CredentialReferenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CapabilityProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(EndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(RegionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(EndpointOrigin);
        if (!Uri.TryCreate(EndpointOrigin, UriKind.Absolute, out Uri? endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("Google project/model authorization requires an explicit HTTPS endpoint origin.");
        }
        if (nowUtc.Offset != TimeSpan.Zero || CapturedAtUtc.Offset != TimeSpan.Zero || ExpiresAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Google project/model authorization timestamps must be UTC.");
        if (CapturedAtUtc > nowUtc || ExpiresAtUtc <= CapturedAtUtc)
            throw new InvalidOperationException("Google project/model authorization has an invalid validity window.");
        return this;
    }

    public bool IsCurrent(DateTimeOffset nowUtc) =>
        Authorized && CapturedAtUtc <= nowUtc && nowUtc < ExpiresAtUtc;
}
