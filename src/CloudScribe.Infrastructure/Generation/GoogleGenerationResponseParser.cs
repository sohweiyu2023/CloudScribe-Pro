using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationParsedResponse(ReadOnlyMemory<byte> AudioBytes, string? ProviderOperationId);

public static class GoogleGenerationResponseParser
{
    public static GoogleGenerationParsedResponse Parse(ReadOnlyMemory<byte> body, int maximumAudioBytes = 64 * 1024 * 1024)
    {
        if (body.IsEmpty) throw new InvalidDataException("Google response body is empty.");
        if (maximumAudioBytes is <= 0 or > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumAudioBytes));

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Google response root must be a JSON object.");

        if (!root.TryGetProperty("audioContent", out var audioElement) || audioElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Google response did not contain audioContent.");

        var encoded = audioElement.GetString();
        if (string.IsNullOrWhiteSpace(encoded)) throw new InvalidDataException("Google audioContent is empty.");

        var maximumEncodedChars = checked(((maximumAudioBytes + 2) / 3) * 4 + 4);
        if (encoded.Length > maximumEncodedChars)
            throw new InvalidDataException("Google audioContent exceeds the configured decoded media bound.");

        byte[] audio;
        try { audio = Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new InvalidDataException("Google audioContent is not valid base64.", ex); }

        if (audio.Length == 0 || audio.Length > maximumAudioBytes)
            throw new InvalidDataException("Decoded Google audioContent is outside the allowed media bounds.");

        string? operationId = null;
        if (root.TryGetProperty("operationId", out var operationElement) && operationElement.ValueKind == JsonValueKind.String)
        {
            operationId = operationElement.GetString();
            if (string.IsNullOrWhiteSpace(operationId)) operationId = null;
        }

        return new GoogleGenerationParsedResponse(audio, operationId);
    }
}
