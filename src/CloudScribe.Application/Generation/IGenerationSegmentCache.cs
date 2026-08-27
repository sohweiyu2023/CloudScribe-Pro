using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public interface IGenerationSegmentCache
{
    Task<bool> ContainsAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default);

    Task<byte[]?> ReadAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default);

    Task StoreAsync(ContentAddressedSegmentKey key, ReadOnlyMemory<byte> mediaBytes, CancellationToken cancellationToken = default);
}
