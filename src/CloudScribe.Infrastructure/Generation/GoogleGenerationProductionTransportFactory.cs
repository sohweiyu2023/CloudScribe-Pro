using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationProductionTransportFactory
{
    private readonly HttpClient _httpClient;
    private readonly ITransientCredentialResolver _credentialResolver;
    private readonly TimeProvider _timeProvider;

    public GoogleGenerationProductionTransportFactory(
        HttpClient httpClient,
        ITransientCredentialResolver credentialResolver,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public GoogleGenerationProductionTransport Create(
        GoogleGenerationProductionEvidence evidence,
        GoogleGenerationAccount submissionAccount)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(submissionAccount);
        evidence.Validate(_timeProvider.GetUtcNow());
        submissionAccount.Validate();

        ProviderAccountSnapshot snapshot = evidence.Account;
        var reference = snapshot.Reference;
        if (reference.CredentialReference is null)
            throw new InvalidOperationException("Current Google provider account has no credential reference.");
        if (reference.EndpointOrigin is null)
            throw new InvalidOperationException("Current Google provider account has no admitted endpoint origin.");
        if (string.IsNullOrWhiteSpace(reference.RegionId))
            throw new InvalidOperationException("Current Google provider account has no admitted region identity.");

        Uri submissionOrigin = new(submissionAccount.Endpoint.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        if (!string.Equals(submissionAccount.AccountId, reference.AccountId, StringComparison.Ordinal)
            || !string.Equals(submissionAccount.CredentialReferenceId, reference.CredentialReference.TargetName, StringComparison.Ordinal)
            || !string.Equals(submissionAccount.Region, reference.RegionId, StringComparison.Ordinal)
            || Uri.Compare(
                submissionOrigin,
                reference.EndpointOrigin,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidOperationException(
                "Exact Google synthesis endpoint binding does not match the current persisted provider-account evidence.");
        }

        var transport = new GoogleGenerationHttpTransport(
            _httpClient,
            _credentialResolver,
            submissionAccount.Endpoint);

        return new GoogleGenerationProductionTransport(submissionAccount, transport);
    }
}
