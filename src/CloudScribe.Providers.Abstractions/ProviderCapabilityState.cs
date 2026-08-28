namespace CloudScribe.Providers.Abstractions;

public enum ProviderCapabilityState
{
    Unknown = 0,
    Supported = 1,
    Unsupported = 2,
    Degraded = 3,
}
