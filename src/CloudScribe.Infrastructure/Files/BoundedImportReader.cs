namespace CloudScribe.Infrastructure.Files;

internal sealed class BoundedImportReader
{
    private readonly int _maxBytes = BoundedImportRequestValidator.MaxSourceBytes;

    public async Task<byte[]> ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new(64 * 1024);
        byte[] chunk = new byte[64 * 1024];
        int total = 0;
        while (true)
        {
            int read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            total = checked(total + read);
            if (total > _maxBytes)
            {
                throw new InvalidDataException("Content exceeds the configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
