using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Local identity check only. Does not fetch a token, prove Long Audio voice support or grant bucket access.</summary>
public sealed class GoogleLongAudioAccountReadiness(IProviderAccountStore accounts, GoogleServiceAccountCredentialOnboardingService onboarding,
    CompletionWorkflowGate? completionGate = null)
{
    public async ValueTask RequireCurrentAsync(GoogleLongAudioRequest request, string accountId, string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        completionGate?.RequireReady();
        var account = await accounts.FindAsync(GoogleGenerationProvider.StableProviderId, accountId, cancellationToken).ConfigureAwait(false);
        if (account is not { IsEnabled: true } || !string.Equals(account.Reference.AccountId, accountId, StringComparison.Ordinal)
            || !string.Equals(account.Reference.CredentialReference?.TargetName, request.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The enabled Google account or its existing credential identity changed.");
        var identity = await onboarding.ResolveIdentityAsync(request.CredentialReferenceId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(identity.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(identity.CredentialReferenceId, request.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The configured service account belongs to a different reviewed project.");
        Uri endpoint = await onboarding.ResolveSynthesisEndpointAsync(request.CredentialReferenceId, cancellationToken).ConfigureAwait(false);
        if (account.Reference.EndpointOrigin is null
            || Uri.Compare(account.Reference.EndpointOrigin, new Uri(endpoint.GetLeftPart(UriPartial.Authority)),
                UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
            throw new InvalidOperationException("The existing Google account endpoint identity changed.");
    }
}
