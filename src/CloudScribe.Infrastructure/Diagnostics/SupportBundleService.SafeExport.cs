using System.Buffers;

namespace CloudScribe.Infrastructure.Diagnostics;

public sealed partial class SupportBundleService
{
    private static async Task SanitizeCapturedAsync(Stream source, Stream destination, long capturedBytes,
        CancellationToken token)
    {
        byte[] block = ArrayPool<byte>.Shared.Rent(81920);
        byte[] line = ArrayPool<byte>.Shared.Rent(SafeDiagnosticExportEncoder.MaximumRecordBytes);
        try
        {
            long remaining = capturedBytes; int length = 0; bool oversized = false;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length, remaining)), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("A diagnostic source changed before its captured bytes could be sanitized.");
                remaining -= read;
                for (int index = 0; index < read; index++)
                {
                    if (block[index] != 10)
                    {
                        if (length < SafeDiagnosticExportEncoder.MaximumRecordBytes) line[length++] = block[index];
                        else oversized = true;
                        continue;
                    }
                    if (length == 0 && !oversized) continue;
                    byte[] encoded = oversized ? SafeDiagnosticExportEncoder.Omitted("OversizedRecord")
                        : SafeDiagnosticExportEncoder.Encode(line.AsMemory(0, length));
                    await WriteMetadataAsync(destination, encoded, token).ConfigureAwait(false);
                    length = 0; oversized = false;
                }
            }
            if (length != 0 || oversized)
                await WriteMetadataAsync(destination, SafeDiagnosticExportEncoder.Omitted("TruncatedRecord"), token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block, clearArray: true);
            ArrayPool<byte>.Shared.Return(line, clearArray: true);
        }
    }

    private static async Task WriteMetadataAsync(Stream destination, byte[] encoded, CancellationToken token)
    {
        if (encoded.Length > SafeDiagnosticExportEncoder.MaximumRecordBytes) encoded = SafeDiagnosticExportEncoder.Omitted("OversizedRecord");
        await destination.WriteAsync(encoded, token).ConfigureAwait(false);
        await destination.WriteAsync(new byte[] { 10 }, token).ConfigureAwait(false);
    }
}
