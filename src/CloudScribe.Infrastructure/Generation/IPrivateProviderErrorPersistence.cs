namespace CloudScribe.Infrastructure.Generation;

/// <summary>Local private-record boundary. Receives only an already-redacted bounded record, never transport context.</summary>
public interface IPrivateProviderErrorPersistence
{
    bool Save(PrivateProviderErrorRecord record);
    IReadOnlyList<PrivateProviderErrorRecord> Read();
}
