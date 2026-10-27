namespace CloudScribe.Application.Generation;

public sealed record ArticleHistoricalImportPreview(Guid PreviewId, string ReviewSha256,
    Guid DocumentId, int Ordinal, string VoiceName, string LanguageCode, long AudioBytes);
