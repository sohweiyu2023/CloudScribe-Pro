using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationProductionAccountFactory
{
    private readonly TimeProvider _timeProvider;

    public GoogleGenerationProductionAccountFactory(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public GoogleGenerationAccount Create(GoogleGenerationProductionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        GoogleGenerationProductionEvidence validated = evidence.Validate(_timeProvider.GetUtcNow());
        ProviderAccountReference account = validated.Account.Reference;

        CredentialReference credential = account.CredentialReference
            ?? throw new InvalidOperationException("Current Google provider account has no credential reference.");
        Uri endpoint = account.EndpointOrigin
            ?? throw new InvalidOperationException("Current Google provider account has no admitted endpoint origin.");
        string region = account.RegionId
            ?? throw new InvalidOperationException("Current Google provider account has no admitted region identity.");

        return new GoogleGenerationAccount(
            account.AccountId,
            credential.TargetName,
            endpoint,
            region).Validate();
    }
}
