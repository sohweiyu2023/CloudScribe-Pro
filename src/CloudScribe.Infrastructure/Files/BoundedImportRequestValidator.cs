using CloudScribe.Application.Documents;

namespace CloudScribe.Infrastructure.Files;

internal static class BoundedImportRequestValidator
{
    public const int MaxSourceBytes = 32 * 1024 * 1024;

    public static void Validate(LocalDocumentImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ArgumentException("Display name is required.", nameof(request));
        }

        if (request.Content is null || !request.Content.CanRead)
        {
            throw new ArgumentException("Content must be readable.", nameof(request));
        }

        if (request.DeclaredLength is < 0 or > MaxSourceBytes)
        {
            throw new InvalidDataException("Content exceeds the configured size limit.");
        }
    }
}
