using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Classifies complete exact error responses before redaction. Persists only an enum, never message text.</summary>
public static class GoogleLongAudioFailureClassifier
{
    public static GoogleLongAudioFailureKind Classify(int httpStatus, int? rpcCode, ReadOnlyMemory<byte> body,
        GoogleResponseReadState readState)
    {
        if (httpStatus is 401 or 403 || rpcCode is 7 or 16) return GoogleLongAudioFailureKind.AuthenticationOrPermission;
        if (httpStatus == 429 || rpcCode == 8) return GoogleLongAudioFailureKind.QuotaOrRateLimited;
        if (httpStatus == 408 || httpStatus >= 500 || rpcCode is 4 or 13 or 14) return GoogleLongAudioFailureKind.TransientOrTimeout;
        if (readState != GoogleResponseReadState.Complete || body.IsEmpty || body.Length > 16_384)
            return GoogleLongAudioFailureKind.Unknown;
        if (!(httpStatus == 400 && rpcCode is null || httpStatus == 200 && rpcCode == 3))
            return GoogleLongAudioFailureKind.Unknown;
        try
        {
            using var parsed = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            GoogleLongAudioValidation.RequireUnique(parsed.RootElement);
            var error = parsed.RootElement.GetProperty("error");
            if (error.ValueKind != JsonValueKind.Object) return GoogleLongAudioFailureKind.Unknown;
            if (rpcCode is null && error.TryGetProperty("code", out var restCode) && (!restCode.TryGetInt32(out int restValue) || restValue != 400))
                return GoogleLongAudioFailureKind.Unknown;
            if (rpcCode == 3 && error.TryGetProperty("status", out var namedStatus)
                && (namedStatus.ValueKind != JsonValueKind.String || !string.Equals(namedStatus.GetString(), "INVALID_ARGUMENT", StringComparison.Ordinal)))
                return GoogleLongAudioFailureKind.Unknown;
            if (rpcCode == 3 && (!parsed.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True
                || !error.TryGetProperty("code", out var code) || !code.TryGetInt32(out int value) || value != 3))
                return GoogleLongAudioFailureKind.Unknown;
            // Operation RPC errors do not contain REST's status string. Add only its already-validated safe code mapping.
            byte[] diagnostic = rpcCode == 3 ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                error = new
                {
                    status = "INVALID_ARGUMENT",
                    message = error.TryGetProperty("message", out var message) ? message : default,
                    details = error.TryGetProperty("details", out var details) ? details : JsonSerializer.SerializeToElement(Array.Empty<string>())
                },
            }) : body.ToArray();
            return GoogleErrorResponseDiagnosticParser.Parse(diagnostic).MessageHint == GoogleValidationMessageHint.SentenceLengthReported
                ? GoogleLongAudioFailureKind.SentenceLengthReported : GoogleLongAudioFailureKind.OtherInputRejected;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { return GoogleLongAudioFailureKind.Unknown; }
    }
}
