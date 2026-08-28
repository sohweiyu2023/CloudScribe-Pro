using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Providers;

public sealed record StoredProviderCapabilitySnapshot
{
    public StoredProviderCapabilitySnapshot(
        Guid id,
        ProviderCapabilitySnapshot snapshot,
        DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Stored capability snapshots require a non-empty id.", nameof(id));
        }
        ArgumentNullException.ThrowIfNull(snapshot);
        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Capability expiry timestamps must be UTC.", nameof(expiresAtUtc));
        }
        if (expiresAtUtc < snapshot.CapturedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Capability evidence cannot expire before capture.");
        }

        Id = id;
        Snapshot = snapshot;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; }
    public ProviderCapabilitySnapshot Snapshot { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsStale(DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Capability staleness checks require UTC.", nameof(nowUtc));
        }
        return nowUtc >= ExpiresAtUtc;
    }
}
