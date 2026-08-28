namespace CloudScribe.Providers.Abstractions;

public enum ProviderLifecycleState
{
    Unknown = 0,
    Available = 1,
    Preview = 2,
    Deprecated = 3,
    Retired = 4,
}
