using System.Buffers.Binary;

namespace CloudScribe.Domain.Generation;

public static class ReturnedMediaMetadataInspector
{
    public static bool TryInspectWav(ReadOnlySpan<byte> bytes, out CacheReuseMediaMetadata? metadata)
    {
        metadata = null;
        if (bytes.Length < 44 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
            return false;

        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        uint dataBytes = 0;

        for (var offset = 12; offset <= bytes.Length - 8;)
        {
            var id = bytes.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var payloadOffset = offset + 8;
            if ((ulong)payloadOffset + size > (ulong)bytes.Length) return false;

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                var format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(payloadOffset, 2));
                if (format != 1) return false;
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(payloadOffset + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(payloadOffset + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(payloadOffset + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataBytes = size;
            }

            var padded = size + (size & 1U);
            var next = (ulong)payloadOffset + padded;
            if (next > int.MaxValue || next <= (ulong)offset) return false;
            offset = (int)next;
        }

        if (channels == 0 || sampleRate == 0 || bitsPerSample == 0 || dataBytes == 0) return false;
        var bytesPerSecond = (long)sampleRate * channels * bitsPerSample / 8L;
        if (bytesPerSecond <= 0) return false;
        var durationMs = checked((long)Math.Round(dataBytes * 1000d / bytesPerSecond, MidpointRounding.AwayFromZero));
        if (durationMs <= 0) return false;

        metadata = new CacheReuseMediaMetadata(GenerationAudioFormat.Wav, checked((int)sampleRate), channels, durationMs).Validate();
        return true;
    }
}
