namespace CloudScribe.Application.Documents;

public sealed record NarrationPartMember(Guid DocumentId, Guid RevisionId, string Sha256);
