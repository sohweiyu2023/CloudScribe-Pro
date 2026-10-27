using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read only a bounded canonical error.status; never return message, details or body.</summary>
public static class GoogleErrorStatusParser
{
    public static GoogleErrorStatus Parse(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty) return GoogleErrorStatus.NotProvided;
        if (body.Length > 16 * 1024) return GoogleErrorStatus.Unknown;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return GoogleErrorStatus.Unknown;
            var errors = root.EnumerateObject().Where(property => property.NameEquals("error")).ToArray();
            if (errors.Length == 0) return GoogleErrorStatus.NotProvided;
            if (errors.Length != 1 || errors[0].Value.ValueKind != JsonValueKind.Object) return GoogleErrorStatus.Unknown;
            var statuses = errors[0].Value.EnumerateObject().Where(property => property.NameEquals("status")).ToArray();
            if (statuses.Length == 0) return GoogleErrorStatus.NotProvided;
            if (statuses.Length != 1 || statuses[0].Value.ValueKind != JsonValueKind.String) return GoogleErrorStatus.Unknown;
            return statuses[0].Value.GetString() switch
            {
                "INVALID_ARGUMENT" => GoogleErrorStatus.InvalidArgument,
                "UNAUTHENTICATED" => GoogleErrorStatus.Unauthenticated,
                "PERMISSION_DENIED" => GoogleErrorStatus.PermissionDenied,
                "RESOURCE_EXHAUSTED" => GoogleErrorStatus.ResourceExhausted,
                "FAILED_PRECONDITION" => GoogleErrorStatus.FailedPrecondition,
                "NOT_FOUND" => GoogleErrorStatus.NotFound,
                "UNIMPLEMENTED" => GoogleErrorStatus.Unimplemented,
                "INTERNAL" => GoogleErrorStatus.Internal,
                "UNAVAILABLE" => GoogleErrorStatus.Unavailable,
                "DEADLINE_EXCEEDED" => GoogleErrorStatus.DeadlineExceeded,
                _ => GoogleErrorStatus.Unknown,
            };
        }
        catch (JsonException) { return GoogleErrorStatus.Unknown; }
    }
}
