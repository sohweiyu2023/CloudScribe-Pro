using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Security;

/// <summary>Presence metadata only; unknown/empty material is never reported as absent.</summary>
public interface ICredentialPresenceReader
{
    ValueTask<CredentialRolePresence> InspectAsync(CredentialReference reference, CancellationToken cancellationToken = default);
}
