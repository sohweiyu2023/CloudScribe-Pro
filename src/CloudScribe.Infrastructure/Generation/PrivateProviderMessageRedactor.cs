using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Bounded known-echo redaction, not an anonymity guarantee. Remaining provider text is always private.</summary>
public static class PrivateProviderMessageRedactor
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    public static string? Extract(ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> input, string token)
    {
        var diagnostic = GoogleErrorResponseDiagnosticParser.Parse(body);
        if (diagnostic.Parse != GoogleErrorBodyParseState.ValidError || diagnostic.Message != GoogleErrorMemberState.TextValue || diagnostic.Status != GoogleErrorMemberState.TextValue
            || body.Length > 16384 || input.Length > 16384 || token.Length > 16384) return null;
        using var json = JsonDocument.Parse(body);
        var error = json.RootElement.GetProperty("error");
        if (!error.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "INVALID_ARGUMENT", StringComparison.Ordinal)) return null;
        string message = error.GetProperty("message").GetString()!;
        if (string.IsNullOrWhiteSpace(message)) return null;
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
        return string.Concat(message.EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) is not UnicodeCategory.Control and not UnicodeCategory.Format));
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
