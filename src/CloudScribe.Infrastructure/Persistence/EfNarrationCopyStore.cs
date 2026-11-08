using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Persistence;

public sealed partial class EfNarrationCopyStore(
    IDbContextFactory<CloudScribeDbContext> factory,
    DocumentContentStore contentStore,
    TimeProvider timeProvider,
    ILogger<EfNarrationCopyStore>? logger = null) : INarrationCopyStore
{
    private const string TransformSettings = "skip-urls=true";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<DocumentSnapshot> ImportBatchSourceAsync(
        Guid batchId, Guid itemId, string rawSha256, string title, string normalizedText,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(batchId);
        RequireIdentity(itemId);
        RequireHash(rawSha256);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(normalizedText);
        string sourceTitle = title.Trim();
        if (sourceTitle.Length is < 1 or > 240)
            throw new ArgumentException("Document title must contain 1 to 240 characters.", nameof(title));
        string textHash = Hash(normalizedText);
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // SQLite's non-deferred transaction acquires the writer lock before any pin/source read.
        // Competing instances therefore observe the winning committed pin, never publish a second document.
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var prior = await context.Set<BatchSourceImportEntity>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.BatchId == batchId && row.ItemId == itemId, cancellationToken).ConfigureAwait(false);
        string key = $"batch-source-v1/{batchId:N}/{itemId:N}";
        Guid documentId = StableId(key + "/document");
        Guid revisionId = StableId(key + "/revision");
        if (prior is not null)
        {
            if (!string.Equals(prior.RawSha256, rawSha256, StringComparison.Ordinal)
                || !string.Equals(prior.SourceTitle, sourceTitle, StringComparison.Ordinal)
                || !string.Equals(prior.SourceTextSha256, textHash, StringComparison.Ordinal)
                || prior.SourceDocumentId != documentId || prior.SourceRevisionId != revisionId)
                throw new InvalidOperationException("The batch source pin changed; the original import was not replaced.");
            var existing = await ReadPinnedAsync(context, documentId, revisionId, textHash, true, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing.Title, sourceTitle, StringComparison.Ordinal)
                || !string.Equals(existing.Text, normalizedText, StringComparison.Ordinal))
                throw new InvalidOperationException("The imported source changed; it was not reused.");
            return existing;
        }

        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var source = await AddDocumentAsync(context, documentId, revisionId, sourceTitle, normalizedText,
            "Batch source import", $"batch-source-v1;batch={batchId:N};item={itemId:N};raw-sha256={rawSha256}", now, cancellationToken).ConfigureAwait(false);
        context.Add(new BatchSourceImportEntity
        {
            BatchId = batchId,
            ItemId = itemId,
            RawSha256 = rawSha256,
            SourceTitle = sourceTitle,
            SourceDocumentId = documentId,
            SourceRevisionId = revisionId,
            SourceTextSha256 = textHash,
            CreatedAtUnixMilliseconds = now,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return source;
    }

    public async Task<NarrationCopyPreparation> PrepareAsync(
        Guid sourceDocumentId, Guid sourceRevisionId, string sourceTextSha256,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await PrepareCoreAsync(sourceDocumentId, sourceRevisionId, sourceTextSha256, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (BatchDiagnostics.NonFatal(error))
        {
            BatchDiagnostics.Failure(logger, null, sourceDocumentId, BatchDiagnosticOperation.PrepareNarration, error);
            throw;
        }
    }

    private async Task<NarrationCopyPreparation> PrepareCoreAsync(
        Guid sourceDocumentId, Guid sourceRevisionId, string sourceTextSha256,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(sourceDocumentId);
        RequireIdentity(sourceRevisionId);
        RequireHash(sourceTextSha256);
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        bool importedSource = await context.Set<BatchSourceImportEntity>().AsNoTracking()
            .AnyAsync(row => row.SourceDocumentId == sourceDocumentId, cancellationToken).ConfigureAwait(false);
        var source = await ReadPinnedAsync(context, sourceDocumentId, sourceRevisionId, sourceTextSha256, importedSource, cancellationToken).ConfigureAwait(false);
        var transformed = NarrationUrlOmission.Transform(source.Text);
        if (transformed.OmittedUrlCount == 0)
        {
            BatchDiagnostics.Source(logger, null, sourceDocumentId, BatchSourceOutcome.NarrationUnchanged);
            return new(sourceDocumentId, sourceRevisionId, sourceTextSha256, source, 0, transformed.TransformVersion);
        }
        if (!transformed.HasSpeakableContent)
        {
            BatchDiagnostics.Source(logger, null, sourceDocumentId, BatchSourceOutcome.NarrationEmpty);
            throw new InvalidOperationException("Skipping URLs leaves no speakable text; no narration copy was created.");
        }

        var prior = await context.Set<NarrationCopyEntity>().AsNoTracking().SingleOrDefaultAsync(
            row => row.SourceDocumentId == sourceDocumentId && row.SourceRevisionId == sourceRevisionId
                && row.TransformVersion == transformed.TransformVersion && row.TransformSettings == TransformSettings,
            cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            var reused = await ReadPreparationAsync(context, source, sourceTextSha256, transformed, prior, cancellationToken).ConfigureAwait(false);
            BatchDiagnostics.Source(logger, null, sourceDocumentId, BatchSourceOutcome.NarrationReused);
            return reused;
        }

        var prepared = await CreatePreparationAsync(context, source, sourceTextSha256, transformed, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        BatchDiagnostics.Source(logger, null, sourceDocumentId, BatchSourceOutcome.NarrationPrepared);
        return prepared;
    }

    private async Task<NarrationCopyPreparation> CreatePreparationAsync(
        CloudScribeDbContext context, DocumentSnapshot source, string sourceTextSha256,
        NarrationUrlOmissionResult transformed, CancellationToken cancellationToken)
    {
        Guid sourceDocumentId = source.Id;
        Guid sourceRevisionId = source.CurrentRevisionId!.Value;
        var sourceRevision = await context.DocumentRevisions.AsNoTracking().SingleAsync(
            row => row.Id == sourceRevisionId && row.DocumentId == sourceDocumentId, cancellationToken).ConfigureAwait(false);
        string key = CopyKey(sourceDocumentId, sourceRevisionId, sourceTextSha256, transformed.TransformVersion);
        Guid childId = StableId(key + "/document");
        Guid childRevisionId = StableId(key + "/revision");
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        string title = NarrationTitle(source.Title);
        var child = await AddDocumentAsync(context, childId, childRevisionId, title, transformed.OutputText,
            "URLs skipped for narration", transformed.TransformVersion, now, cancellationToken).ConfigureAwait(false);
        context.Add(new NarrationCopyEntity
        {
            SourceDocumentId = sourceDocumentId,
            SourceRevisionId = sourceRevisionId,
            SourceTextSha256 = sourceTextSha256,
            SourceContentRelativePath = sourceRevision.ContentRelativePath,
            SourceContentByteLength = sourceRevision.ContentByteLength,
            TransformVersion = transformed.TransformVersion,
            TransformSettings = TransformSettings,
            ChildDocumentId = childId,
            ChildRevisionId = childRevisionId,
            ChildTextSha256 = Hash(transformed.OutputText),
            OmittedUrlCount = transformed.OmittedUrlCount,
            CreatedAtUnixMilliseconds = now,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(sourceDocumentId, sourceRevisionId, sourceTextSha256, child, transformed.OmittedUrlCount, transformed.TransformVersion);
    }

    public async Task<NarrationCopyPreparation?> FindAsync(
        Guid narrationDocumentId, CancellationToken cancellationToken = default)
    {
        RequireIdentity(narrationDocumentId);
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // A deferred read transaction gives one coherent snapshot without requiring write access.
        using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
        using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        var prior = await context.Set<NarrationCopyEntity>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.ChildDocumentId == narrationDocumentId, cancellationToken).ConfigureAwait(false);
        if (prior is null)
            return null;
        var source = await ReadPinnedAsync(context, prior.SourceDocumentId, prior.SourceRevisionId,
            prior.SourceTextSha256, false, cancellationToken).ConfigureAwait(false);
        return await ReadPreparationAsync(context, source, prior.SourceTextSha256,
            NarrationUrlOmission.Transform(source.Text), prior, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NarrationCopyPreparation> ReadPreparationAsync(
        CloudScribeDbContext context, DocumentSnapshot source, string sourceHash,
        NarrationUrlOmissionResult transformed, NarrationCopyEntity prior, CancellationToken cancellationToken)
    {
        var sourceRevision = await context.DocumentRevisions.AsNoTracking().SingleAsync(
            row => row.Id == source.CurrentRevisionId && row.DocumentId == source.Id, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(prior.SourceContentRelativePath, sourceRevision.ContentRelativePath, StringComparison.Ordinal)
            || prior.SourceContentByteLength != sourceRevision.ContentByteLength)
            throw new InvalidDataException("The source revision durable content metadata changed after preparation.");
        string key = CopyKey(source.Id, source.CurrentRevisionId!.Value, sourceHash, transformed.TransformVersion);
        if (prior.SourceDocumentId != source.Id || prior.SourceRevisionId != source.CurrentRevisionId
            || !string.Equals(prior.SourceTextSha256, sourceHash, StringComparison.Ordinal)
            || !string.Equals(prior.TransformVersion, transformed.TransformVersion, StringComparison.Ordinal)
            || !string.Equals(prior.TransformSettings, TransformSettings, StringComparison.Ordinal)
            || prior.ChildDocumentId != StableId(key + "/document") || prior.ChildRevisionId != StableId(key + "/revision")
            || !string.Equals(prior.ChildTextSha256, Hash(transformed.OutputText), StringComparison.Ordinal)
            || prior.OmittedUrlCount != transformed.OmittedUrlCount || prior.OmittedUrlCount < 1 || !transformed.HasSpeakableContent)
            throw new InvalidOperationException("Narration copy provenance does not match the exact source and URL settings.");
        var child = await ReadPinnedAsync(context, prior.ChildDocumentId, prior.ChildRevisionId,
            prior.ChildTextSha256, true, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(child.Text, transformed.OutputText, StringComparison.Ordinal))
            throw new InvalidOperationException("The narration copy differs from its prepared text.");
        return new(source.Id, prior.SourceRevisionId, sourceHash, child, prior.OmittedUrlCount, prior.TransformVersion);
    }
}
