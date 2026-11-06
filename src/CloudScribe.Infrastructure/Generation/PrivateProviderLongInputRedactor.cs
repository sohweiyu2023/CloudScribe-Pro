using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Matches known echoes by indexing the bounded response, then scanning article variants once.
/// Full input exists only during synchronous preparation; the persistence worker receives redacted records.</summary>
internal sealed class PrivateProviderLongInputRedactor
{
    private static readonly byte[] EmptyInput = "{\"input\":{\"text\":\"\"}}"u8.ToArray();
    private readonly HashSet<string> matches = new(StringComparer.Ordinal);
    private readonly string[] shortVariants;
    private readonly string token;

    internal PrivateProviderLongInputRedactor(ReadOnlyMemory<byte> input, ReadOnlyMemory<byte> body, string transientToken)
    {
        token = transientToken;
        using var request = JsonDocument.Parse(input);
        var content = request.RootElement.GetProperty("input");
        string raw = content.TryGetProperty("text", out var text) ? text.GetString()! : content.GetProperty("ssml").GetString()!;
        string normalized = Normalize(raw);
        string[] variants = new[] { raw, normalized, JsonSerializer.Serialize(raw)[1..^1], JsonSerializer.Serialize(normalized)[1..^1],
            Uri.EscapeDataString(raw), Uri.EscapeDataString(normalized), Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized)) }.Distinct(StringComparer.Ordinal).ToArray();
        shortVariants = raw.EnumerateRunes().Take(8).Count() < 8 ? variants : [];
        var windows = new HashSet<string>(StringComparer.Ordinal);
        AddWindows(new UTF8Encoding(false, true).GetString(body.Span));
        try
        {
            using var response = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            Visit(response.RootElement);
        }
        catch (JsonException) { /* Malformed response still has its strict UTF-8 raw representation indexed. */ }
        foreach (string variant in variants)
        {
            if (windows.Count == 0) break;
            var runes = Normalize(variant).EnumerateRunes().ToArray();
            for (int i = 0; i <= runes.Length - 8 && windows.Count > 0; i++)
            {
                string candidate = Window(runes.AsSpan(i, 8));
                if (windows.Remove(candidate)) matches.Add(candidate);
            }
        }

        void AddWindows(string value)
        {
            var runes = Normalize(value).EnumerateRunes().ToArray();
            for (int i = 0; i <= runes.Length - 8; i++) windows.Add(Window(runes.AsSpan(i, 8)));
        }
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) { AddWindows(property.Name); Visit(property.Value); }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var element in value.EnumerateArray()) Visit(element);
            else if (value.ValueKind == JsonValueKind.String) AddWindows(value.GetString()!);
        }
    }

    internal DiagnosticRedactionResult Redact(string value)
    {
        string normalized = Normalize(value);
        foreach (string known in shortVariants)
            if (known.Length > 0) normalized = normalized.Replace(Normalize(known), "[known echo redacted]", StringComparison.Ordinal);
        var runes = normalized.EnumerateRunes().ToArray(); var masked = new bool[runes.Length];
        for (int i = 0; i <= runes.Length - 8; i++)
            if (matches.Contains(Window(runes.AsSpan(i, 8)))) Array.Fill(masked, true, i, 8);
        var result = new StringBuilder(); bool redacting = false;
        for (int i = 0; i < runes.Length; i++)
        {
            if (masked[i]) { if (!redacting) result.Append("[input fragment redacted]"); redacting = true; }
            else { result.Append(runes[i].ToString()); redacting = false; }
        }
        // Retain the established credential/markup/control handling and explicit truncation result.
        return PrivateProviderMessageRedactor.RedactText(result.ToString(), EmptyInput, token);
    }

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC).ReplaceLineEndings("\n");
    private static string Window(ReadOnlySpan<Rune> runes)
    {
        Span<char> chars = stackalloc char[16]; int length = 0;
        foreach (var rune in runes) length += rune.EncodeToUtf16(chars[length..]);
        return new string(chars[..length]);
    }
}
