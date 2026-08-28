using CloudScribe.Application.Documents;

namespace CloudScribe.Infrastructure.Files;

public sealed class BoundedLocalDocumentImporter : ILocalDocumentImporter
{
    private readonly BoundedLocalImportPipeline _pipeline = new();

    public Task<LocalDocumentImportResult> ImportAsync(
        LocalDocumentImportRequest request,
        CancellationToken cancellationToken = default) =>
        _pipeline.ImportAsync(request, cancellationToken);
}
