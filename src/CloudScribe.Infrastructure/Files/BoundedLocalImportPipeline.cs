using CloudScribe.Application.Documents;

namespace CloudScribe.Infrastructure.Files;

internal sealed class BoundedLocalImportPipeline
{
    private readonly BoundedImportReader _reader = new();

    public async Task<LocalDocumentImportResult> ImportAsync(
        LocalDocumentImportRequest request,
        CancellationToken cancellationToken)
    {
        BoundedImportRequestValidator.Validate(request);
        byte[] source = await _reader.ReadAsync(request.Content, cancellationToken).ConfigureAwait(false);
        return BoundedImportResultFactory.Build(request, source);
    }
}
