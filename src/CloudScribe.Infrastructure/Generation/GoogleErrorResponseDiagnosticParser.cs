using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Observability only: no returned field participates in provider, retry or financial policy.</summary>
public static partial class GoogleErrorResponseDiagnosticParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static GoogleErrorResponseDiagnostics Parse(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty) return Unexamined(GoogleErrorBodyParseState.Empty);
        if (body.Length > 16384) return Unexamined(GoogleErrorBodyParseState.OverDiagnosticBound);
        try
        {
            _ = StrictUtf8.GetCharCount(body.Span);
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            if (!Unique(json.RootElement)) return Unexamined(GoogleErrorBodyParseState.Ambiguous);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return Unexamined(GoogleErrorBodyParseState.InvalidErrorShape);
            if (!json.RootElement.TryGetProperty("error", out var error)) return Unexamined(GoogleErrorBodyParseState.MissingError);
            if (error.ValueKind != JsonValueKind.Object) return Unexamined(GoogleErrorBodyParseState.InvalidErrorShape);
            var status = Member(error, "status", out string? statusText);
            var message = Member(error, "message", out string? messageText);
            var (details, count) = DescribeDetails(error);
            if (details == GoogleErrorDetailsState.Ambiguous)
                return new(GoogleErrorBodyParseState.Ambiguous, status, message, messageText?.Length ?? 0, details, count, GoogleValidationMessageHint.NotExamined);
            var hint = message switch
            {
                GoogleErrorMemberState.Absent => GoogleValidationMessageHint.NotProvided,
                GoogleErrorMemberState.TextValue when status == GoogleErrorMemberState.TextValue
                    && string.Equals(statusText, "INVALID_ARGUMENT", StringComparison.Ordinal)
                    && details != GoogleErrorDetailsState.Invalid => Hint(messageText!),
                _ => GoogleValidationMessageHint.NotExamined,
            };
            return new(GoogleErrorBodyParseState.ValidError, status, message, messageText?.Length ?? 0, details, count, hint);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        { return Unexamined(GoogleErrorBodyParseState.InvalidJson); }
    }

    private static GoogleErrorResponseDiagnostics Unexamined(GoogleErrorBodyParseState state) =>
        new(state, GoogleErrorMemberState.NotExamined, GoogleErrorMemberState.NotExamined, 0,
            GoogleErrorDetailsState.NotExamined, 0, GoogleValidationMessageHint.NotExamined);

    private static GoogleErrorMemberState Member(JsonElement value, string name, out string? text)
    {
        text = null;
        if (!value.TryGetProperty(name, out var member)) return GoogleErrorMemberState.Absent;
        if (member.ValueKind != JsonValueKind.String) return GoogleErrorMemberState.Invalid;
        text = member.GetString();
        return GoogleErrorMemberState.TextValue;
    }

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) if (!Unique(item)) return false;
        }
        else if (value.ValueKind == JsonValueKind.String)
            _ = value.GetString(); // Validate JSON-escaped surrogate pairs too; discard the bounded value.
        return true;
    }
}
