using System.Buffers;

namespace CloudScribe.Infrastructure.Diagnostics;

public sealed partial class SupportBundleService
{
    private sealed class SafeExportBudget(long maximum)
    {
        private long _remaining = maximum;
        public void Consume(int count)
        {
            if (count < 0 || count > _remaining) throw new IOException("Sanitized diagnostic metadata exceeds the configured bundle limit.");
            _remaining -= count;
        }
    }

    private static async Task SanitizeCapturedAsync(Stream source, Stream destination, long capturedBytes,
        SafeExportBudget budget, CancellationToken token)
    {
        byte[] block = ArrayPool<byte>.Shared.Rent(81920);
        byte[] line = ArrayPool<byte>.Shared.Rent(SafeDiagnosticExportEncoder.MaximumRecordBytes);
        try
        {
            long remaining = capturedBytes; int length = 0, records = 0; bool oversized = false;
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
                    if (++records > 4096)
                    { await WriteMetadataAsync(destination, SafeDiagnosticExportEncoder.Omitted("RecordLimit"), budget, token).ConfigureAwait(false); return; }
                    byte[] encoded = oversized ? SafeDiagnosticExportEncoder.Omitted("OversizedRecord")
                        : SafeDiagnosticExportEncoder.Encode(line.AsMemory(0, length));
                    await WriteMetadataAsync(destination, encoded, budget, token).ConfigureAwait(false);
                    length = 0; oversized = false;
                }
            }
            if (length != 0 || oversized)
                await WriteMetadataAsync(destination, SafeDiagnosticExportEncoder.Omitted("TruncatedRecord"), budget, token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block, clearArray: true);
            ArrayPool<byte>.Shared.Return(line, clearArray: true);
        }
    }

    private static async Task WriteMetadataAsync(Stream destination, byte[] encoded, SafeExportBudget budget, CancellationToken token)
    {
        if (encoded.Length > SafeDiagnosticExportEncoder.MaximumRecordBytes) encoded = SafeDiagnosticExportEncoder.Omitted("OversizedRecord");
        budget.Consume(checked(encoded.Length + 1));
        await destination.WriteAsync(encoded, token).ConfigureAwait(false);
        await destination.WriteAsync(new byte[] { 10 }, token).ConfigureAwait(false);
    }
}
