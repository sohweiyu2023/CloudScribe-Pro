using System.Text.Json;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class StrictJsonObjectReader
{
    public const int DefaultMaximumDocumentBytes = 8 * 1024 * 1024;
    public const int DefaultMaximumDepth = 64;

    private readonly int _maximumDocumentBytes;
    private readonly int _maximumDepth;

    public StrictJsonObjectReader(
        int maximumDocumentBytes = DefaultMaximumDocumentBytes,
        int maximumDepth = DefaultMaximumDepth)
    {
        if (maximumDocumentBytes is < 2 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDocumentBytes));
        }
        if (maximumDepth is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }
        _maximumDocumentBytes = maximumDocumentBytes;
        _maximumDepth = maximumDepth;
    }

    public JsonDocument Parse(ReadOnlyMemory<byte> utf8Json)
    {
        ValidateDocumentSize(utf8Json);
        ValidateStrictTokens(utf8Json.Span);
        return ParseValidatedDocument(utf8Json);
    }

    private void ValidateDocumentSize(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw new PricingCatalogFormatException(PricingCatalogFormatError.Empty, "Pricing catalog JSON is empty.");
        }
        if (utf8Json.Length > _maximumDocumentBytes)
        {
            throw new PricingCatalogFormatException(
                PricingCatalogFormatError.TooLarge,
                $"Pricing catalog JSON exceeds the {_maximumDocumentBytes}-byte safety limit.");
        }
    }

    private void ValidateStrictTokens(ReadOnlySpan<byte> utf8Json)
    {
        Utf8JsonReader reader = new(utf8Json, CreateReaderOptions());
        Stack<HashSet<string>> objectPropertyNames = new();
        bool firstToken = true;
        try
        {
            while (reader.Read())
            {
                ValidateFirstToken(ref reader, ref firstToken);
                TrackObjectPropertyNames(ref reader, objectPropertyNames);
            }
        }
        catch (PricingCatalogFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidJson(exception, reader.TokenStartIndex);
        }

        if (firstToken || objectPropertyNames.Count != 0)
        {
            throw new PricingCatalogFormatException(PricingCatalogFormatError.InvalidJson, "Pricing catalog JSON is incomplete.");
        }
    }

    private static void ValidateFirstToken(ref Utf8JsonReader reader, ref bool firstToken)
    {
        if (!firstToken)
        {
            return;
        }

        firstToken = false;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new PricingCatalogFormatException(
                PricingCatalogFormatError.TopLevelNotObject,
                "Pricing catalog JSON must contain exactly one top-level object.",
                reader.TokenStartIndex);
        }
    }

    private static void TrackObjectPropertyNames(
        ref Utf8JsonReader reader,
        Stack<HashSet<string>> objectPropertyNames)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                objectPropertyNames.Push(new HashSet<string>(StringComparer.Ordinal));
                break;
            case JsonTokenType.PropertyName:
                TrackPropertyName(ref reader, objectPropertyNames);
                break;
            case JsonTokenType.EndObject:
                EndObject(ref reader, objectPropertyNames);
                break;
        }
    }

    private static void TrackPropertyName(
        ref Utf8JsonReader reader,
        Stack<HashSet<string>> objectPropertyNames)
    {
        if (objectPropertyNames.Count == 0)
        {
            throw new PricingCatalogFormatException(
                PricingCatalogFormatError.InvalidJson,
                "A JSON property appeared outside an object.",
                reader.TokenStartIndex);
        }

        string propertyName = reader.GetString()
            ?? throw new PricingCatalogFormatException(
                PricingCatalogFormatError.InvalidJson,
                "JSON property name could not be decoded.",
                reader.TokenStartIndex);
        if (!objectPropertyNames.Peek().Add(propertyName))
        {
            throw new PricingCatalogFormatException(
                PricingCatalogFormatError.DuplicateProperty,
                $"Duplicate JSON property is not permitted: {propertyName}",
                reader.TokenStartIndex,
                propertyName);
        }
    }

    private static void EndObject(
        ref Utf8JsonReader reader,
        Stack<HashSet<string>> objectPropertyNames)
    {
        if (objectPropertyNames.Count == 0)
        {
            throw new PricingCatalogFormatException(
                PricingCatalogFormatError.InvalidJson,
                "JSON object nesting is invalid.",
                reader.TokenStartIndex);
        }
        _ = objectPropertyNames.Pop();
    }

    private JsonDocument ParseValidatedDocument(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            return JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = _maximumDepth,
            });
        }
        catch (JsonException exception)
        {
            throw InvalidJson(exception);
        }
    }

    private JsonReaderOptions CreateReaderOptions() => new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = _maximumDepth,
    };

    private static PricingCatalogFormatException InvalidJson(JsonException exception, long? bytePosition = null) =>
        new(
            PricingCatalogFormatError.InvalidJson,
            "Pricing catalog is not strict UTF-8 JSON.",
            bytePosition,
            innerException: exception);
}
