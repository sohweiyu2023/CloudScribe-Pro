namespace CloudScribe.Application.Batch.Authority;

/// <summary>How a batch may obtain spending authority. Set once by an administrator in the installer-pinned profile.</summary>
public enum BatchAuthorityMode
{
    /// <summary>Batches can be prepared and rehearsed, but never narrated.</summary>
    Off,
    /// <summary>Default. Each batch exports one request; a reviewer installs the consent and the article admission for all its files.</summary>
    Reviewed,
    /// <summary>One click in the app approves a batch, but only inside the installed per-batch and rolling ceilings.</summary>
    InAppWithinLimits,
}
