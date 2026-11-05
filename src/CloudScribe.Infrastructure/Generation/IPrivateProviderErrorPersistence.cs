namespace CloudScribe.Infrastructure.Generation;

/// <summary>Local private-record boundary. Receives only an already-redacted bounded record, never transport context.</summary>
public interface IPrivateProviderErrorPersistence
{
    bool Save(PrivateProviderErrorRecord record);
    IReadOnlyList<PrivateProviderErrorRecord> Read();
    PrivateProviderErrorReadResult ReadWithStatus() => new(PrivateProviderErrorReadStatus.Success, Read());
    PrivateProviderErrorReadResult ReadWithStatus(Guid recordId) => ReadWithStatus();
    /// <summary>Explicit-opt-in preflight only. Unsupported stores fail closed; no credential or provider action.</summary>
    bool PrepareProtectedCapture() => false;
}
