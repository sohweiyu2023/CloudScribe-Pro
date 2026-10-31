using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Existing receipt and its independently resolved native parent coverage; does not modify or reimport the audio.</summary>
public sealed record ChirpReusedArticleAudio(Guid ParentDocumentId, Guid ParentRevisionId,
    string ReceiptSha256, ChirpNarrationRange Range, ArticleAcceptedAudio Audio,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? SourcePlanSha256 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? SourceAuthorizationId = null);
