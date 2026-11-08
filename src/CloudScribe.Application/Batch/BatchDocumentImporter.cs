using System.Security.Cryptography;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Batch;

/// <summary>Reads selected TXT files through the normal importer and adds each as its own library document,
/// exactly like a single import (same decoding, size limit and line-ending normalisation; no other changes).
/// Each created document is recorded in the saved queue before the next file, so an interrupted import never
/// creates a second copy of a document that was already added.</summary>
public sealed class BatchDocumentImporter(ILocalDocumentImporter importer, DocumentPreprocessor preprocessor,
    IDocumentLibrary library, IBatchQueueStore store, ILogger? logger = null)
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

    /// <summary>Reads a listed file again from where it was added and returns its text only if its bytes are unchanged.
    /// A missing path, a read failure or a changed file returns a reason instead, so different text is never narrated
    /// under an earlier review.</summary>
    public async Task<(string? Text, string? ReasonCode)> ReadAgainAsync(BatchItem item, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrEmpty(item.SourcePath)) return (null, "batch.import-interrupted-add-again");
        BatchSourceFile read;
        try
        {
            var stream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
                read = await ReadAsync(item.SourceFileName, stream, maximumBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return (null, "batch.source-missing-add-again"); }
        if (!string.Equals(read.Sha256, item.SourceSha256, StringComparison.Ordinal)) return (null, "batch.source-changed-add-again");
        return read.Text is null ? (null, "batch.source-missing-add-again") : (read.Text, null);
    }

    /// <summary>Checks a pending saved import against its pinned bytes, even when decoded text is cached.
    /// Only files without a local path may use their in-memory text.</summary>
    public Task<(string? Text, string? ReasonCode)> ReadPendingAsync(BatchItem item, IReadOnlyDictionary<Guid, string> cachedText,
        long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(cachedText);
        return string.IsNullOrEmpty(item.SourcePath) && cachedText.TryGetValue(item.ItemId, out string? text)
            ? Task.FromResult<(string?, string?)>((text, null))
            : ReadAgainAsync(item, maximumBytes, cancellationToken);
    }

    /// <summary>Reads the current source for an unreviewed draft. Local files always come from disk, not cache;
    /// changed bytes may be adopted before saving and review. Missing or invalid sources have no usable text.</summary>
    public async Task<BatchSourceFile> ReadDraftSourceAsync(BatchItem item, IReadOnlyDictionary<Guid, string> cachedText,
        long maximumBytes, CancellationToken cancellationToken = default)
    {
        var source = await ReadDraftSourceCoreAsync(item, cachedText, maximumBytes, cancellationToken).ConfigureAwait(false);
        var outcome = string.IsNullOrWhiteSpace(source.Text) ? BatchSourceOutcome.SourceUnavailable
            : string.Equals(source.Sha256, item.SourceSha256, StringComparison.Ordinal) ? BatchSourceOutcome.DraftSourceReady : BatchSourceOutcome.DraftSourceRefreshed;
        BatchDiagnostics.Source(logger, null, item.ItemId, outcome);
        return source;
    }

    private async Task<BatchSourceFile> ReadDraftSourceCoreAsync(BatchItem item, IReadOnlyDictionary<Guid, string> cachedText,
        long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(cachedText);
        if (string.IsNullOrEmpty(item.SourcePath))
            return new(item.SourceFileName, item.SourceSha256, item.SourceBytes,
                cachedText.GetValueOrDefault(item.ItemId), SourcePath: item.SourcePath);
        try
        {
            var stream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
                return await ReadAsync(item.SourceFileName, stream, maximumBytes, cancellationToken).ConfigureAwait(false) with { SourcePath = item.SourcePath };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(item.SourceFileName, string.Empty, 0, null, BatchExclusion.Unreadable, item.SourcePath); }
    }

    /// <summary>Adds every included file that is not yet in the library. <paramref name="textByItem"/> holds the decoded
    /// text from <see cref="ReadAsync"/>; it is never written to the queue file.</summary>
    public Task<NarrationBatch> ImportAsync(NarrationBatch queue, IReadOnlyDictionary<Guid, string> textByItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textByItem);
        return ImportAsync(queue, (item, _) => Task.FromResult<(string?, string?)>(textByItem.TryGetValue(item.ItemId, out string? text)
            ? (text, null) : (null, "batch.import-interrupted-add-again")), null, cancellationToken);
    }

    /// <summary>Adds every included file that is not yet in the library, asking <paramref name="textFor"/> for each
    /// file's text (from memory, or read again from disk with its hash checked). Files left over by an interrupted add
    /// are picked up again. Each document is recorded in the saved queue before the next file, so a cancelled or
    /// interrupted add never creates a second copy. <paramref name="progress"/> reports (files done, files to add).</summary>
    public async Task<NarrationBatch> ImportAsync(NarrationBatch queue, Func<BatchItem, CancellationToken, Task<(string? Text, string? ReasonCode)>> textFor,
        IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(textFor);
        var pending = queue.Items.Where(NeedsImport).OrderBy(x => x.Order).ToArray();
        int done = 0;
        progress?.Report((0, pending.Length));
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (text, reason) = await textFor(item, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                queue = await store.SaveAsync(queue.WithItem(item with { State = BatchItemState.Failed, ReasonCode = reason ?? "batch.import-interrupted-add-again" }),
                    CancellationToken.None).ConfigureAwait(false);
                BatchDiagnostics.Source(logger, queue.BatchId, item.ItemId, reason switch
                {
                    "batch.source-changed-add-again" => BatchSourceOutcome.SourceChanged,
                    "batch.source-missing-add-again" => BatchSourceOutcome.SourceUnavailable,
                    null or "batch.import-interrupted-add-again" => BatchSourceOutcome.ImportInterrupted,
                    _ => BatchSourceOutcome.ImportRefused,
                });
                progress?.Report((++done, pending.Length));
                continue;
            }
            var preview = preprocessor.Preview(text, new(NormalizeLineEndings: true, CollapseHorizontalWhitespace: false,
                CollapseExcessBlankLines: false, SimplifyUrls: false));
            string title = BatchPlanner.TitleOf(item.SourceFileName);
            var created = await library.CreateAsync(title, preview.OutputText, CancellationToken.None).ConfigureAwait(false);
            var saved = await library.SaveAsync(new(created.Id, created.Title, preview.OutputText, created.ConcurrencyVersion,
                DocumentRevisionKind.Import, "Imported source (batch)", "batch:" + item.SourceSha256), CancellationToken.None).ConfigureAwait(false);
            queue = await store.SaveAsync(queue.WithItem(item with { State = BatchItemState.Ready, ReasonCode = null, DocumentId = saved.Id, RevisionId = saved.CurrentRevisionId }),
                CancellationToken.None).ConfigureAwait(false);
            BatchDiagnostics.Source(logger, queue.BatchId, item.ItemId, BatchSourceOutcome.Imported);
            progress?.Report((++done, pending.Length));
        }
        return queue;
    }

    /// <summary>True for a file that still has to be added to the library: waiting without a document, or left failed
    /// by an interrupted or refused add (and never started).</summary>
    public static bool NeedsImport(BatchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.DocumentId is not null || item.JobId is not null) return false;
        return item.State == BatchItemState.Ready
            || item.State == BatchItemState.Failed && item.ReasonCode is "batch.import-interrupted-add-again" or "batch.source-changed-add-again" or "batch.source-missing-add-again";
    }
}
