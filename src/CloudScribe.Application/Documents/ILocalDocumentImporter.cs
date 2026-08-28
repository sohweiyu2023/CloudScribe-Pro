namespace CloudScribe.Application.Documents;

public interface ILocalDocumentImporter
{
    Task<LocalDocumentImportResult> ImportAsync(LocalDocumentImportRequest request, CancellationToken cancellationToken = default);
}
