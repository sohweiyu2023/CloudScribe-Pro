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
        ValidateRequiredText(AccountId, nameof(AccountId));
        ValidateRequiredText(ProjectId, nameof(ProjectId));
        ValidateRequiredText(ModelId, nameof(ModelId));
        ValidateRequiredText(CredentialReferenceId, nameof(CredentialReferenceId));
        ValidateRequiredText(CapabilityProvenanceId, nameof(CapabilityProvenanceId));
        ValidateRequiredText(EndpointId, nameof(EndpointId));
        ValidateRequiredText(RegionId, nameof(RegionId));
        ValidateRequiredText(EndpointOrigin, nameof(EndpointOrigin));
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

    private static void ValidateRequiredText(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Google project/model authorization is missing {propertyName}.");
    }
}
