namespace CloudScribe.Application.Documents;


/// <summary>Additive presentation provenance only; never authorization or synthesis evidence.</summary>
public interface INarrationPartGroupStore
{
    Task<NarrationPartPreparation> BeginAsync(Guid parentId, Guid sourceRevisionId, string sourceSha256,
        IReadOnlyList<string> orderedPartHashes, CancellationToken cancellationToken = default);
    Task RegisterAsync(Guid groupId, IReadOnlyList<NarrationPartMember> parts, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NarrationLibraryArticle>> QueryLibraryAsync(string query, int limit = 200, CancellationToken cancellationToken = default);
}
