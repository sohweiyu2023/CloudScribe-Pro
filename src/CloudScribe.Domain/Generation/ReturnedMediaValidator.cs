namespace CloudScribe.Domain.Generation;

public static class ReturnedMediaValidator
{
    public const int DefaultMaximumMediaBytes = 64 * 1024 * 1024;

    public static ReturnedMediaValidationResult Validate(
        ReadOnlySpan<byte> mediaBytes,
        string? contentType,
        int maximumMediaBytes = DefaultMaximumMediaBytes)
    {
        if (maximumMediaBytes < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMediaBytes));
        }

        if (mediaBytes.IsEmpty)
        {
            return ReturnedMediaValidationResult.Invalid("media.empty", "Provider returned no media bytes.");
        }

        if (mediaBytes.Length > maximumMediaBytes)
        {
            return ReturnedMediaValidationResult.Invalid("media.too-large", "Returned media exceeds the configured bounded payload limit.");
        }

        if (LooksLikeWave(mediaBytes))
        {
            if (!string.IsNullOrWhiteSpace(contentType) && !contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("wave", StringComparison.OrdinalIgnoreCase))
            {
                return ReturnedMediaValidationResult.Invalid("media.content-type-mismatch", "WAV bytes conflict with the provider content type.");
            }

            return ValidateWave(mediaBytes);
        }

        if (LooksLikeMp3(mediaBytes))
        {
            if (!string.IsNullOrWhiteSpace(contentType) && !contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("mp3", StringComparison.OrdinalIgnoreCase))
            {
                return ReturnedMediaValidationResult.Invalid("media.content-type-mismatch", "MP3 bytes conflict with the provider content type.");
            }

            return ReturnedMediaValidationResult.Valid(GenerationAudioFormat.Mp3);
        }

        return ReturnedMediaValidationResult.Invalid("media.unsupported-or-corrupt", "Returned bytes do not contain a recognized supported audio container signature.");
    }

    private static bool LooksLikeWave(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8);

    private static ReturnedMediaValidationResult ValidateWave(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44)
        {
            return ReturnedMediaValidationResult.Invalid("media.wav-truncated", "WAV payload is shorter than the minimum PCM header.");
        }

        var declaredRiffPayload = BitConverter.ToUInt32(bytes.Slice(4, 4));
        if ((ulong)declaredRiffPayload + 8UL > (ulong)bytes.Length)
        {
            return ReturnedMediaValidationResult.Invalid("media.wav-truncated", "WAV RIFF length exceeds available provider bytes.");
        }

        if (!ContainsChunk(bytes, "fmt "u8) || !ContainsChunk(bytes, "data"u8))
        {
            return ReturnedMediaValidationResult.Invalid("media.wav-missing-chunk", "WAV payload is missing required fmt or data chunk metadata.");
        }

        return ReturnedMediaValidationResult.Valid(GenerationAudioFormat.Wav);
    }

    private static bool ContainsChunk(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> id)
    {
        for (var offset = 12; offset <= bytes.Length - 8;)
        {
            var chunkId = bytes.Slice(offset, 4);
            var size = BitConverter.ToUInt32(bytes.Slice(offset + 4, 4));
            if (chunkId.SequenceEqual(id))
            {
                return true;
            }

            var padded = (ulong)size + ((size & 1U) == 0 ? 0UL : 1UL);
            var next = (ulong)offset + 8UL + padded;
            if (next > int.MaxValue || next <= (ulong)offset || next > (ulong)bytes.Length)
            {
                return false;
            }

            offset = (int)next;
        }

        return false;
    }

    private static bool LooksLikeMp3(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual("ID3"u8))
        {
            return bytes.Length >= 10;
        }

        return bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0;
    }
}
