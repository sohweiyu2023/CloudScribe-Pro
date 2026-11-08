using System.Security.Cryptography;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;

namespace CloudScribe.Application.Batch;

/// <summary>Reads selected TXT files through the normal importer and adds each as its own library document,
/// exactly like a single import (same decoding, size limit and line-ending normalisation; no other changes).
/// Each created document is recorded in the saved queue before the next file, so an interrupted import never
/// creates a second copy of a document that was already added.</summary>
public sealed class BatchDocumentImporter(ILocalDocumentImporter importer, DocumentPreprocessor preprocessor,
    IDocumentLibrary library, IBatchQueueStore store)
{
    /// <summary>Decodes one selected file without adding anything to the library.</summary>
    public async Task<BatchSourceFile> ReadAsync(string fileName, Stream content, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return new(fileName, string.Empty, 0, null, BatchExclusion.Unsupported);
        byte[] bytes;
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maximumBytes) return new(fileName, string.Empty, buffer.Length + read, null, BatchExclusion.TooLarge);
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            bytes = buffer.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(fileName, string.Empty, 0, null, BatchExclusion.Unreadable); }
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.Length == 0) return new(fileName, sha, 0, null, BatchExclusion.Empty);
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            var imported = await importer.ImportAsync(new(LocalDocumentImportKind.PlainText, fileName, source, bytes.LongLength), cancellationToken).ConfigureAwait(false);
            return new(fileName, sha, bytes.LongLength, imported.Text);
        }
        catch (InvalidDataException) { return new(fileName, sha, bytes.LongLength, null, BatchExclusion.NotUnicodeText); }
        catch (ArgumentException) { return new(fileName, sha, bytes.LongLength, null, BatchExclusion.Unreadable); }
    }

    /// <summary>Adds every included file that is not yet in the library. <paramref name="textByItem"/> holds the decoded
    /// text from <see cref="ReadAsync"/>; it is never written to the queue file.</summary>
    public async Task<NarrationBatch> ImportAsync(NarrationBatch queue, IReadOnlyDictionary<Guid, string> textByItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(textByItem);
        foreach (var item in queue.Items.Where(x => x.IsIncluded && x.DocumentId is null).OrderBy(x => x.Order).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!textByItem.TryGetValue(item.ItemId, out string? text))
            {
                // The app closed before this file was added; its text is no longer in memory.
                queue = await store.SaveAsync(queue.WithItem(item with { State = BatchItemState.Failed, ReasonCode = "batch.import-interrupted-add-again" }),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            var preview = preprocessor.Preview(text, new(NormalizeLineEndings: true, CollapseHorizontalWhitespace: false,
                CollapseExcessBlankLines: false, SimplifyUrls: false));
            string title = BatchPlanner.TitleOf(item.SourceFileName);
            var created = await library.CreateAsync(title, preview.OutputText, cancellationToken).ConfigureAwait(false);
            var saved = await library.SaveAsync(new(created.Id, created.Title, preview.OutputText, created.ConcurrencyVersion,
                DocumentRevisionKind.Import, "Imported source (batch)", "batch:" + item.SourceSha256), CancellationToken.None).ConfigureAwait(false);
            queue = await store.SaveAsync(queue.WithItem(item with { DocumentId = saved.Id, RevisionId = saved.CurrentRevisionId }),
                CancellationToken.None).ConfigureAwait(false);
        }
        return queue;
    }
}
