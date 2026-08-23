using System.Buffers.Binary;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5ReturnedMediaMetadataInspectorTests
{
    [Fact]
    public void Pcm_wav_metadata_is_derived_for_cache_reuse()
    {
        var wav = BuildPcmWav(sampleRate: 24000, channels: 1, bitsPerSample: 16, dataBytes: 48000);
        Assert.True(ReturnedMediaMetadataInspector.TryInspectWav(wav, out var metadata));
        Assert.NotNull(metadata);
        Assert.Equal(GenerationAudioFormat.Wav, metadata!.Format);
        Assert.Equal(24000, metadata.SampleRateHz);
        Assert.Equal(1, metadata.ChannelCount);
        Assert.Equal(1000, metadata.DurationMilliseconds);
    }

    [Fact]
    public void Non_pcm_or_truncated_wav_fails_closed()
    {
        Assert.False(ReturnedMediaMetadataInspector.TryInspectWav(new byte[12], out _));
        var wav = BuildPcmWav(24000, 1, 16, 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 3);
        Assert.False(ReturnedMediaMetadataInspector.TryInspectWav(wav, out _));
    }

    private static byte[] BuildPcmWav(int sampleRate, ushort channels, ushort bitsPerSample, int dataBytes)
    {
        var bytes = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 8)));
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), checked((uint)sampleRate));
        var byteRate = checked((uint)(sampleRate * channels * bitsPerSample / 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), checked((ushort)(channels * bitsPerSample / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), bitsPerSample);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), checked((uint)dataBytes));
        return bytes;
    }
}
