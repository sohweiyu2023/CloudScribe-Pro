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

    public GoogleGenerationProductionTransport Create(GoogleGenerationProductionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate(_timeProvider.GetUtcNow());

        ProviderAccountSnapshot snapshot = evidence.Account;
        var reference = snapshot.Reference;
        if (reference.CredentialReference is null)
            throw new InvalidOperationException("Current Google provider account has no credential reference.");
        if (reference.EndpointOrigin is null)
            throw new InvalidOperationException("Current Google provider account has no admitted endpoint origin.");
        if (string.IsNullOrWhiteSpace(reference.RegionId))
            throw new InvalidOperationException("Current Google provider account has no admitted region identity.");

        var account = new GoogleGenerationAccount(
            reference.AccountId,
            reference.CredentialReference.TargetName,
            reference.EndpointOrigin,
            reference.RegionId).Validate();

        var transport = new GoogleGenerationHttpTransport(
            _httpClient,
            _credentialResolver,
            account.Endpoint);

        return new GoogleGenerationProductionTransport(account, transport);
    }
}
