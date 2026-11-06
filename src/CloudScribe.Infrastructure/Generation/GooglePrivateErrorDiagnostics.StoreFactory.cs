namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GooglePrivateErrorDiagnostics
{
    /// <summary>One current-user encrypted store shared by the supported provider workflows and private viewer.</summary>
    public static IPrivateProviderErrorPersistence OpenPrivateStore(string directory, TimeProvider clock) => new FilePrivateProviderErrorStore(directory, clock);
}
