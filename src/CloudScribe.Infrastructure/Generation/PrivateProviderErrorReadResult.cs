namespace CloudScribe.Infrastructure.Generation;

public sealed record PrivateProviderErrorReadResult(PrivateProviderErrorReadStatus Status, IReadOnlyList<PrivateProviderErrorRecord> Records)
{
    public override string ToString() => $"Private diagnostic read: {Status}; record contents omitted";
}
