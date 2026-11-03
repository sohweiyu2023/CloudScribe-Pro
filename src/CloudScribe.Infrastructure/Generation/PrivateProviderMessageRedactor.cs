using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Bounded known-echo redaction, not an anonymity guarantee. Remaining provider text is always private.</summary>
public static class PrivateProviderMessageRedactor
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    public static string? Extract(ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> input, string token)
        => ExtractDetailed(body, input, token)?.Message;

    internal static PrivateProviderExplanationText? ExtractDetailed(ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> input, string token)
    {
        var diagnostic = GoogleErrorResponseDiagnosticParser.Parse(body);
        if (diagnostic.Parse != GoogleErrorBodyParseState.ValidError || diagnostic.Status != GoogleErrorMemberState.TextValue
            || body.Length > 16384 || input.Length > 16384 || token.Length > 16384) return null;
        using var json = JsonDocument.Parse(body);
        var error = json.RootElement.GetProperty("error");
        if (!error.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "INVALID_ARGUMENT", StringComparison.Ordinal)) return null;
        var explanation = CollectExplanation(error);
        string message = explanation.Message;
        if (string.IsNullOrWhiteSpace(message)) return null;
        var sanitized = RedactText(message, input, token);
        return new(sanitized.Text, explanation.DetailsOmitted, sanitized.WasTruncated);
    }

    internal static DiagnosticRedactionResult RedactText(string message, ReadOnlyMemory<byte> input, string token)
    {
        if (input.Length > 16384 || token.Length > 16384 || message.Length > 32768)
            throw new InvalidDataException("Private redaction input exceeds its bound.");
        using var request = JsonDocument.Parse(input);
        var content = request.RootElement.GetProperty("input");
        string rawText = content.TryGetProperty("text", out var plain) ? plain.GetString()!
            : content.GetProperty("ssml").GetString()!;
        message = message.Normalize(NormalizationForm.FormC).ReplaceLineEndings("\n");
        string text = rawText.Normalize(NormalizationForm.FormC).ReplaceLineEndings("\n");
        message = RemoveKnown(message, token);
        message = RemoveKnown(message, token.Normalize(NormalizationForm.FormC));
        message = RemoveKnown(message, text);
        // Also suppress contiguous known input fragments; operate only on bounded request/message data.
        foreach (string variant in new[] { text, rawText, JsonSerializer.Serialize(text)[1..^1], JsonSerializer.Serialize(rawText)[1..^1], Uri.EscapeDataString(text), Uri.EscapeDataString(rawText), Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), Convert.ToBase64String(Encoding.UTF8.GetBytes(rawText)) }.Distinct(StringComparer.Ordinal))
            message = RemoveFragments(message, variant);
        message = Replace(message, @"(?i)\bBearer\s+[^\s\""'<>]+", "[credential redacted]");
        message = Replace(message, @"\bAIza[0-9A-Za-z_-]{20,}\b", "[credential redacted]");
        message = Replace(message, @"(?is)-----BEGIN[^-]{0,40}PRIVATE KEY-----.*?-----END[^-]{0,40}PRIVATE KEY-----", "[credential redacted]");
        message = Replace(message, @"(?i)(access_token|refresh_token|api[_-]?key|password|authorization|token)[\""']?\s*[=:]\s*[\""']?[^\s,;<>]+", "[credential redacted]");
        message = Replace(message, @"<[^>]{0,2048}>", "[markup removed]");
        string inert = string.Concat(message.EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) is not UnicodeCategory.Control and not UnicodeCategory.Format));
        var sanitized = DiagnosticRedactor.SanitizePrivateProviderExplanation(inert);
        return sanitized;
    }

    private static PrivateProviderExplanationText CollectExplanation(JsonElement error)
    {
        List<string> messages = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool omitted = false;
        void Visit(JsonElement node, int depth)
        {
            if (depth > 8) { omitted = true; return; }
            if (node.ValueKind == JsonValueKind.Array)
            {
                int count = 0;
                foreach (var item in node.EnumerateArray())
                { if (++count > 32) { omitted = true; break; } Visit(item, depth + 1); }
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                int count = 0;
                foreach (var property in node.EnumerateObject())
                {
                    if (++count > 64) { omitted = true; break; }
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Visit(property.Value, depth + 1);
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        if (property.Name is "message" or "description" or "reason" or "domain" or "field")
                        {
                            string text = property.Value.GetString()!;
                            if (string.IsNullOrWhiteSpace(text) || !seen.Add(text)) continue;
                            if (messages.Count >= 32) { omitted = true; continue; }
                            messages.Add(depth == 0 && string.Equals(property.Name, "message", StringComparison.Ordinal) ? text : property.Name + ": " + text);
                        }
                        else if (property.Name is not ("status" or "@type")) omitted = true;
                    }
                    else if (!string.Equals(property.Name, "code", StringComparison.Ordinal)) omitted = true;
                }
            }
            else omitted = true;
        }
        Visit(error, 0);
        return new(string.Join("\nDetail: ", messages), omitted);
    }

    private static string RemoveKnown(string message, string known)
    {
        if (known.Length == 0) return message;
        foreach (string value in new[] { known, JsonSerializer.Serialize(known)[1..^1], Uri.EscapeDataString(known), Convert.ToBase64String(Encoding.UTF8.GetBytes(known)) }.Distinct(StringComparer.Ordinal))
            message = message.Replace(value, "[known echo redacted]", StringComparison.Ordinal);
        return message;
    }

    private static string RemoveFragments(string message, string input)
    {
        const int width = 8;
        var source = input.EnumerateRunes().ToArray(); var target = message.EnumerateRunes().ToArray();
        var windows = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i <= source.Length - width; i++) windows.Add(string.Concat(source.Skip(i).Take(width)));
        var masked = new bool[target.Length];
        for (int i = 0; i <= target.Length - width; i++)
            if (windows.Contains(string.Concat(target.Skip(i).Take(width)))) Array.Fill(masked, true, i, width);
        var result = new StringBuilder(); bool redacting = false;
        for (int i = 0; i < target.Length; i++)
        {
            if (masked[i]) { if (!redacting) result.Append("[input fragment redacted]"); redacting = true; }
            else { result.Append(target[i]); redacting = false; }
        }
        return result.ToString();
    }

    private static string Replace(string text, string pattern, string replacement) => Regex.Replace(text, pattern, replacement, RegexOptions.CultureInvariant, MatchTimeout);
}
