namespace CloudScribe.Application.Generation;

public sealed record ArticleAudioMember(Guid DocumentId, Guid RevisionId, string TextSha256, int Ordinal);
