namespace CloudScribe.Application.Activation;

/// <summary>
/// Identifies the process boundary that produced an activation request.
/// </summary>
public enum ActivationSource
{
    PrimaryLaunch = 0,
    SecondaryInstance = 1,
}
