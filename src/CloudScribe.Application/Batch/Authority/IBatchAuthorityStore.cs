namespace CloudScribe.Application.Batch.Authority;

public interface IBatchAuthorityStore
{
    /// <summary>The installed, read-only administrator profile, or null when none is installed or it fails verification.</summary>
    Task<BatchAuthorityProfile?> LoadProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>A reviewer-installed consent for the batch (read-only), or an in-app consent saved by the user.</summary>
    Task<BatchConsent?> LoadConsentAsync(Guid batchId, CancellationToken cancellationToken = default);

    /// <summary>Saves an in-app consent. Reviewed consents can only be installed by the reviewed installer script.</summary>
    Task SaveInAppConsentAsync(BatchConsent consent, CancellationToken cancellationToken = default);
}
