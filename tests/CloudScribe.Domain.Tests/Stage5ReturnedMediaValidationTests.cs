using System.Buffers.Binary;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5ReturnedMediaValidationTests
{
    [Fact]
    public void ValidPcmWavePassesStructuralValidation()
    {
        var media = CreateWave(16);

        var result = ReturnedMediaValidator.Validate(media, "audio/wav");

        Assert.True(result.IsValid);
        Assert.Equal(GenerationAudioFormat.Wav, result.DetectedFormat);
    }

    [Fact]
    public void TruncatedWaveFailsClosed()
    {
        var media = CreateWave(16);
        Array.Resize(ref media, media.Length - 5);

        var result = ReturnedMediaValidator.Validate(media, "audio/wav");

        Assert.False(result.IsValid);
        Assert.Equal("media.wav-truncated", result.DiagnosticCode);
    }

    [Fact]
    public void ContentTypeConflictFailsClosed()
    {
        var result = ReturnedMediaValidator.Validate(CreateWave(16), "audio/mpeg");

        Assert.False(result.IsValid);
        Assert.Equal("media.content-type-mismatch", result.DiagnosticCode);
    }

    [Fact]
    public void UnknownPayloadFailsClosed()
    {
        var result = ReturnedMediaValidator.Validate(new byte[128], "application/octet-stream");

        Assert.False(result.IsValid);
        Assert.Equal("media.unsupported-or-corrupt", result.DiagnosticCode);
    }

    [Fact]
    public void MasteringProfileRejectsUnsafeOrNonsensicalBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GenerationMasteringProfile("bad", 1m, -16m, 0, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GenerationMasteringProfile("bad", -1m, -80m, 0, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GenerationMasteringProfile("bad", -1m, -16m, -1, 0).Validate());
    }

    private static byte[] CreateWave(int dataLength)
    {
        var bytes = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        "WAVE"u8.CopyTo(bytes.AsSpan(8, 4));
        "fmt "u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        "data"u8.CopyTo(bytes.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), (uint)dataLength);
        return bytes;
    }
}
