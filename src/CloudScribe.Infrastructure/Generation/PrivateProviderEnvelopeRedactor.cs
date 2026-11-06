using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private evidence, never an anonymity guarantee. No unredacted fallback, audio or arbitrary headers.</summary>
internal static class PrivateProviderEnvelopeRedactor
{
    private static readonly string[] SensitiveNames = ["authorization", "cookie", "token", "password", "secret", "privatekey", "apikey", "audiocontent", "audio", "base64"];
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One bounded recursive transform owns per-response completeness state; no persistence or provider effects.")]
    internal static PrivateProviderResponseEnvelope Create(int status, int observedBytes, bool complete, string readState,
        ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> input, string token, bool operationError = false, int maximumInputBytes = 16384, bool storageMetadata = false)
    {
        bool omitted = !complete || body.Length > 16384;
        bool truncated = omitted;
        bool redactionApplied = false;
        string parse = "Unavailable", text = string.Empty;
        if (status == 200 && !operationError && !storageMetadata) parse = "SuccessBodyExcluded";
        else
        {
            try
            {
                var bounded = body[..Math.Min(body.Length, 16384)];
                if (input.Length > maximumInputBytes || token.Length > 16384)
                    throw new InvalidDataException("Private response redaction preparation exceeds its declared input extent.");
                string decoded = new UTF8Encoding(false, true).GetString(bounded.Span);
                var longInput = maximumInputBytes > 16384 ? new PrivateProviderLongInputRedactor(input, bounded, token) : null;
                DiagnosticRedactionResult Redact(string value) => longInput?.Redact(value)
                    ?? PrivateProviderMessageRedactor.RedactText(value, input, token);
                if (operationError && (status != 200 || decoded.Length == 0))
                    throw new InvalidDataException("Operation error capture requires a valid error object.");
                if (decoded.Length == 0) parse = "Empty";
                else
                {
                    try
                    {
                        using var document = JsonDocument.Parse(bounded, new JsonDocumentOptions { MaxDepth = 16 });
                        if (operationError && (status != 200 || document.RootElement.ValueKind != JsonValueKind.Object
                            || !document.RootElement.TryGetProperty("error", out var operationFailure) || operationFailure.ValueKind != JsonValueKind.Object
                            || !operationFailure.TryGetProperty("code", out var operationCode) || operationCode.ValueKind != JsonValueKind.Number
                            || !operationCode.TryGetInt32(out int canonicalCode) || canonicalCode is < 1 or > 16
                            || !operationFailure.TryGetProperty("message", out var operationMessage) || operationMessage.ValueKind != JsonValueKind.String))
                            throw new InvalidDataException("Operation error capture requires a valid error object.");
                        using var stream = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(stream)) Write(document.RootElement, writer, 0);
                        text = Encoding.UTF8.GetString(stream.ToArray()); parse = "Json";
                    }
                    catch (JsonException parseFailure)
                    {
                        if (operationError) throw new InvalidDataException("Operation error capture requires valid JSON.", parseFailure);
                        parse = "NonJsonOrMalformed";
                        // Unstructured base64/audio cannot safely be distinguished from text. Omit rather than expose it.
                        if (ContainsExcludedData(decoded)) { omitted = true; redactionApplied = true; parse = "UnstructuredContentExcluded"; }
                        else
                        {
                            var redacted = Redact(decoded);
                            text = redacted.Text; truncated |= redacted.WasTruncated;
                            redactionApplied |= !string.Equals(text, decoded, StringComparison.Ordinal);
                        }
                    }
                }

                [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Exhaustive bounded JSON-kind transformation shares only response-local flags.")]
                void Write(JsonElement element, Utf8JsonWriter writer, int depth)
                {
                    if (depth > 8) { writer.WriteStringValue("[depth omitted]"); omitted = true; return; }
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.Object:
                            writer.WriteStartObject(); int count = 0; HashSet<string> names = new(StringComparer.Ordinal);
                            HashSet<string> writtenNames = new(StringComparer.Ordinal);
                            foreach (var property in element.EnumerateObject())
                            {
                                if (++count > 64) { omitted = true; break; }
                                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate response members are ambiguous.");
                                if (Sensitive(property.Name)) { omitted = true; redactionApplied = true; continue; }
                                var name = Redact(property.Name);
                                string safeName = name.Text.Length > 128 ? "[name omitted]" : name.Text;
                                if (!writtenNames.Add(safeName)) { omitted = true; continue; }
                                writer.WritePropertyName(safeName);
                                omitted |= name.WasTruncated || name.Text.Length > 128;
                                redactionApplied |= !string.Equals(name.Text, property.Name, StringComparison.Ordinal);
                                // Canonical protocol status is safe structured evidence, not ambiguous encoded content.
                                // All unknown strings still pass through the normal privacy transform.
                                if (property.NameEquals("status") && property.Value.ValueKind == JsonValueKind.String
                                    && CanonicalStatus(property.Value.GetString())) writer.WriteStringValue(property.Value.GetString());
                                else Write(property.Value, writer, depth + 1);
                            }
                            writer.WriteEndObject(); break;
                        case JsonValueKind.Array:
                            if (element.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Number))
                            { writer.WriteStringValue("[numeric array omitted]"); omitted = true; redactionApplied = true; break; }
                            writer.WriteStartArray(); int items = 0;
                            foreach (var item in element.EnumerateArray())
                            { if (++items > 32) { omitted = true; break; } Write(item, writer, depth + 1); }
                            writer.WriteEndArray(); break;
                        case JsonValueKind.String:
                            string value = element.GetString()!;
                            if (value.Length > 16384 || ContainsExcludedData(value)) { writer.WriteStringValue("[content omitted]"); omitted = true; redactionApplied = true; break; }
                            var result = Redact(value);
                            redactionApplied |= !string.Equals(result.Text, value, StringComparison.Ordinal);
                            writer.WriteStringValue(result.Text); truncated |= result.WasTruncated; break;
                        case JsonValueKind.Number:
                            if (element.TryGetInt64(out long number)) writer.WriteNumberValue(number);
                            else { writer.WriteStringValue("[number omitted]"); omitted = true; }
                            break;
                        case JsonValueKind.True: writer.WriteBooleanValue(true); break;
                        case JsonValueKind.False: writer.WriteBooleanValue(false); break;
                        default: writer.WriteNullValue(); break;
                    }
                    if (writer.BytesCommitted + writer.BytesPending > 32768) throw new InvalidDataException("Response structure exceeds its bound.");
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { parse = error is DecoderFallbackException ? "InvalidUtf8" : "RedactionUnavailable"; text = string.Empty; omitted = true; }
        }
        string limited = LimitUtf8(text, 16384);
        return new(1, status, observedBytes, complete, readState, parse, limited, truncated || limited.Length != text.Length,
            omitted, redactionApplied, status == 200 ? GoogleErrorStatus.NotProvided : GoogleErrorStatusParser.Parse(body[..Math.Min(body.Length, 16384)]));
    }

    private static bool Sensitive(string name) => SensitiveNames
        .Any(part => name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Contains(part, StringComparison.OrdinalIgnoreCase));

    private static bool CanonicalStatus(string? value) => value is "INVALID_ARGUMENT" or "UNAUTHENTICATED" or "PERMISSION_DENIED"
        or "RESOURCE_EXHAUSTED" or "FAILED_PRECONDITION" or "NOT_FOUND" or "UNIMPLEMENTED" or "INTERNAL" or "UNAVAILABLE" or "DEADLINE_EXCEEDED";

    private static bool ContainsExcludedData(string value)
    {
        if (value.Contains("audioContent", StringComparison.OrdinalIgnoreCase)
            || value.Contains("data:audio", StringComparison.OrdinalIgnoreCase)
            || value.Contains("data:application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || value.Contains(";base64,", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(value, @"(?:\A|[\s:,;])(?:[A-Za-z0-9+/_-]{2,}={1,2}|[A-Za-z0-9+/_-]{128,})(?:\z|[\s,;])",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return true;
        // Ambiguous encoded values are omitted, including short, URL-safe and line-wrapped base64.
        string[] pieces = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length > 0 && pieces.All(piece => piece.Length >= 4
            && Regex.IsMatch(piece, @"\A[A-Za-z0-9+/_=-]+\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            && (pieces.Length == 1 || pieces.All(piece => piece.Length % 4 == 0));
    }

    internal static string LimitUtf8(string text, int maximum)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        { if (rune.Utf8SequenceLength > maximum) break; maximum -= rune.Utf8SequenceLength; builder.Append(rune); }
        return builder.ToString();
    }
}
