namespace CloudScribe.Infrastructure.Generation;

public interface ITransientCredentialResolver
{
    ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken);
}
