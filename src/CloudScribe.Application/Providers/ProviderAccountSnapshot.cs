using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Providers;

public sealed record ProviderAccountSnapshot
{
    public ProviderAccountSnapshot(
        ProviderAccountReference reference,
        bool isEnabled,
        long revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Provider account creation timestamps must be UTC.", nameof(createdAtUtc));
        }
        if (updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Provider account update timestamps must be UTC.", nameof(updatedAtUtc));
        }
        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedAtUtc), "Provider account updates cannot predate creation.");
        }

        Reference = reference;
        IsEnabled = isEnabled;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public ProviderAccountReference Reference { get; }
    public bool IsEnabled { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
}
