using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationProductionAccountFactory
{
    private readonly TimeProvider _timeProvider;
    private readonly GoogleServiceAccountCredentialOnboardingService _credentialOnboarding;

    public GoogleGenerationProductionAccountFactory(
        TimeProvider timeProvider,
        GoogleServiceAccountCredentialOnboardingService credentialOnboarding)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _credentialOnboarding = credentialOnboarding
            ?? throw new ArgumentNullException(nameof(credentialOnboarding));
    }

    /// <summary>
    /// Resolves the exact synthesis endpoint that was admitted and persisted with the Windows
    /// credential during onboarding. Provider-account persistence intentionally retains only the
    /// non-secret origin for identity comparisons; production submission must never reconstruct
    /// or hard-code an API path from that origin.
    /// </summary>
    public async Task<GoogleGenerationAccount> CreateAsync(
        GoogleGenerationProductionEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        GoogleGenerationProductionEvidence validated = evidence.Validate(_timeProvider.GetUtcNow());
        ProviderAccountReference account = validated.Account.Reference;

        CredentialReference credential = account.CredentialReference
            ?? throw new InvalidOperationException("Current Google provider account has no credential reference.");
        Uri admittedOrigin = account.EndpointOrigin
            ?? throw new InvalidOperationException("Current Google provider account has no admitted endpoint origin.");
        string region = account.RegionId
            ?? throw new InvalidOperationException("Current Google provider account has no admitted region identity.");

        Uri synthesisEndpoint = await _credentialOnboarding
            .ResolveSynthesisEndpointAsync(credential.TargetName, cancellationToken)
            .ConfigureAwait(false);
        Uri resolvedOrigin = new(synthesisEndpoint.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        if (Uri.Compare(
                admittedOrigin,
                resolvedOrigin,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidOperationException(
                "Persisted Google synthesis endpoint no longer matches the admitted provider-account origin.");
        }

        return new GoogleGenerationAccount(
            account.AccountId,
            credential.TargetName,
            synthesisEndpoint,
            region).Validate();
    }
}
