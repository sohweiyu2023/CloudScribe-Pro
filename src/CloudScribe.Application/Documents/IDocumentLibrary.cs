using CloudScribe.Domain.Documents;

namespace CloudScribe.Application.Documents;

public interface IDocumentLibrary
{
    Task<DocumentSnapshot> CreateAsync(
        string title,
        string text,
        CancellationToken cancellationToken = default);

    Task<DocumentSnapshot?> OpenAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentSummary>> ListAsync(
        DocumentStatus status = DocumentStatus.Active,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentSummary>> SearchAsync(
        string query,
        DocumentStatus status = DocumentStatus.Active,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<DocumentSnapshot> SaveAsync(
        DocumentSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentSummary> ChangeStatusAsync(
        Guid documentId,
        DocumentStatus status,
        long expectedConcurrencyVersion,
        CancellationToken cancellationToken = default);
}
