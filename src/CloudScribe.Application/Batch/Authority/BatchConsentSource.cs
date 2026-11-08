namespace CloudScribe.Application.Batch.Authority;

public enum BatchConsentSource
{
    /// <summary>Installed by a reviewer into the read-only install folder.</summary>
    ReviewedInstall,
    /// <summary>Created by a user click in the app, valid only in <see cref="BatchAuthorityMode.InAppWithinLimits"/>.</summary>
    InApp,
}
