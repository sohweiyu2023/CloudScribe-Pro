using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Retains only explicitly allowlisted canonical categories, never free-form provider data.</summary>
public static partial class GoogleErrorDetailParser
{
    public static GoogleErrorDetailCategories Parse(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty) return GoogleErrorDetailCategories.Missing;
        if (body.Length > 16384) return GoogleErrorDetailCategories.Unknown;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var error = Single(json.RootElement, "error");
            if (error.ValueKind == JsonValueKind.Undefined) return GoogleErrorDetailCategories.Missing;
            var details = Single(error, "details");
            if (details.ValueKind == JsonValueKind.Undefined) return GoogleErrorDetailCategories.Missing;
            if (details.ValueKind != JsonValueKind.Array || details.GetArrayLength() > 16) return GoogleErrorDetailCategories.Unknown;
            var result = GoogleErrorDetailCategories.Missing;
            bool hasInfo = false, hasBadRequest = false;
            foreach (var detail in details.EnumerateArray())
            {
                string? type = StringValue(Single(detail, "@type"));
                if (string.Equals(type, "type.googleapis.com/google.rpc.ErrorInfo", StringComparison.Ordinal))
                {
                    if (hasInfo) return GoogleErrorDetailCategories.Unknown;
                    hasInfo = true;
                    string? canonicalReason = StringValue(Single(detail, "reason"));
                    var domain = Domain(StringValue(Single(detail, "domain")));
                    result = result with
                    {
                        Domain = domain,
                        Reason = domain == GoogleErrorDomainCategory.GoogleApis
                        ? Reason(canonicalReason) : GoogleErrorReasonCategory.Unknown
                    };
                }
                else if (string.Equals(type, "type.googleapis.com/google.rpc.BadRequest", StringComparison.Ordinal))
                {
                    if (hasBadRequest) return GoogleErrorDetailCategories.Unknown;
                    hasBadRequest = true;
                    result = result with { Field = Fields(Single(detail, "fieldViolations")) };
                }
            }
            return result;
        }
        catch (JsonException) { return GoogleErrorDetailCategories.Unknown; }
        catch (InvalidDataException) { return GoogleErrorDetailCategories.Unknown; }
    }

    private static JsonElement Single(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid canonical error structure.");
        var found = element.EnumerateObject().Where(x => x.NameEquals(name)).ToArray();
        if (found.Length > 1) throw new InvalidDataException("Ambiguous canonical error structure.");
        return found.Length == 0 ? default : found[0].Value;
    }

    private static string? StringValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        _ => throw new InvalidDataException("Invalid canonical error category."),
    };
}
