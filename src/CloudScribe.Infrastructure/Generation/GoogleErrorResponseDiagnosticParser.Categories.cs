using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static partial class GoogleErrorResponseDiagnosticParser
{
    private static (GoogleErrorDetailsState State, int Count) DescribeDetails(JsonElement error)
    {
        if (!error.TryGetProperty("details", out var details)) return (GoogleErrorDetailsState.Absent, 0);
        if (details.ValueKind != JsonValueKind.Array) return (GoogleErrorDetailsState.Invalid, 0);
        int count = details.GetArrayLength();
        if (count > 16) return (GoogleErrorDetailsState.Invalid, 17); // Bounded sentinel, not an unbounded count.
        if (count == 0) return (GoogleErrorDetailsState.Empty, 0);
        HashSet<string> recognized = new(StringComparer.Ordinal);
        int unknown = 0;
        foreach (var detail in details.EnumerateArray())
        {
            if (detail.ValueKind != JsonValueKind.Object) return (GoogleErrorDetailsState.Invalid, count);
            var state = Member(detail, "@type", out string? type);
            if (state == GoogleErrorMemberState.Invalid) return (GoogleErrorDetailsState.Invalid, count);
            if (type is "type.googleapis.com/google.rpc.ErrorInfo" or "type.googleapis.com/google.rpc.BadRequest")
            {
                if (!recognized.Add(type)) return (GoogleErrorDetailsState.Ambiguous, count);
                if (!ValidRecognizedShape(detail, type)) return (GoogleErrorDetailsState.Invalid, count);
            }
            else unknown++;
        }
        return (recognized.Count == 0 ? GoogleErrorDetailsState.UnrecognizedTypesOnly
            : unknown == 0 ? GoogleErrorDetailsState.RecognizedTypes : GoogleErrorDetailsState.MixedTypes, count);
    }

    private static bool ValidRecognizedShape(JsonElement detail, string type)
    {
        if (string.Equals(type, "type.googleapis.com/google.rpc.ErrorInfo", StringComparison.Ordinal))
            return OptionalText(detail, "reason") && OptionalText(detail, "domain")
                && (!detail.TryGetProperty("metadata", out var metadata) || metadata.ValueKind == JsonValueKind.Object
                    && metadata.EnumerateObject().All(x => x.Value.ValueKind == JsonValueKind.String));
        if (!detail.TryGetProperty("fieldViolations", out var fields)) return true;
        return fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() <= 16
            && fields.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Object && OptionalText(x, "field") && OptionalText(x, "description"));
    }

    private static bool OptionalText(JsonElement value, string name) =>
        !value.TryGetProperty(name, out var member) || member.ValueKind == JsonValueKind.String;

    private static GoogleValidationMessageHint Hint(string message)
    {
        if (message.Length > 2048) return GoogleValidationMessageHint.OverHintBound;
        if (string.Equals(message, "Request contains an invalid argument.", StringComparison.Ordinal))
            return GoogleValidationMessageHint.GenericValidation;
        const string sentence = "This request contains sentences that are too long.";
        if (string.Equals(message, sentence, StringComparison.Ordinal)
            || message.StartsWith(sentence + " Read more", StringComparison.Ordinal)
            || message.StartsWith(sentence + " Consider splitting up long sentences.", StringComparison.Ordinal)
            || message.StartsWith(sentence + " Consider splitting up long sentences with ", StringComparison.Ordinal)
            || message.StartsWith(sentence + " Consider splitting up long sentences or ", StringComparison.Ordinal))
            return GoogleValidationMessageHint.SentenceLengthReported;
        return GoogleValidationMessageHint.Unrecognized;
    }
}
