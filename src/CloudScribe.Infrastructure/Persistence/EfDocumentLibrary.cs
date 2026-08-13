using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class EfDocumentLibrary(
    IDbContextFactory<CloudScribeDbContext> dbContextFactory,
    DocumentContentStore contentStore,
    TimeProvider timeProvider) : IDocumentLibrary
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<DocumentSnapshot> CreateAsync(
        string title,
        string text,
        CancellationToken cancellationToken = default)
    {
        string normalizedTitle = ValidateTitle(title);
        ArgumentNullException.ThrowIfNull(text);

        Guid documentId = Guid.NewGuid();
        Guid revisionId = Guid.NewGuid();
        byte[] bytes = StrictUtf8.GetBytes(text);
        DocumentContentCommit commit = await contentStore
            .CommitAsync(documentId, revisionId, bytes, cancellationToken)
            .ConfigureAwait(false);
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        try
        {
            await using CloudScribeDbContext context = await dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            DocumentEntity document = new()
            {
                Id = documentId,
                Title = normalizedTitle,
                DraftText = text,
                CreatedAtUnixMilliseconds = now,
                UpdatedAtUnixMilliseconds = now,
                Status = (int)DocumentStatus.Active,
                IsFavorite = false,
                CurrentRevisionId = revisionId,
                ConcurrencyVersion = 1,
            };
            DocumentRevisionEntity revision = BuildRevision(
                documentId,
                revisionId,
                now,
                DocumentRevisionKind.Checkpoint,
                "Initial draft",
                text,
                commit,
                importProvenance: null);

            context.Documents.Add(document);
            context.DocumentRevisions.Add(revision);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(document, text);
        }
        catch
        {
            await TryDeleteUnreferencedAsync(commit).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DocumentSnapshot?> OpenAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        await using CloudScribeDbContext context = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DocumentEntity? document = await context.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        string text = document.DraftText;
        if (document.CurrentRevisionId is Guid revisionId)
        {
            DocumentRevisionEntity? revision = await context.DocumentRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == revisionId && item.DocumentId == documentId, cancellationToken)
                .ConfigureAwait(false);
            if (revision is null)
            {
                throw new InvalidDataException("The document references a revision that does not exist.");
            }

            text = await ReadRevisionTextAsync(revision, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(text, document.DraftText, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The durable revision and current document draft disagree.");
            }
        }

        return ToSnapshot(document, text);
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(
        DocumentStatus status = DocumentStatus.Active,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateStatus(status);
        int boundedLimit = ValidateLimit(limit);
        await using CloudScribeDbContext context = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DocumentEntity[] documents = await context.Documents
            .AsNoTracking()
            .Where(item => item.Status == (int)status)
            .OrderByDescending(item => item.UpdatedAtUnixMilliseconds)
            .ThenBy(item => item.Title)
            .Take(boundedLimit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return documents.Select(ToSummary).ToArray();
    }

    public async Task<IReadOnlyList<DocumentSummary>> SearchAsync(
        string query,
        DocumentStatus status = DocumentStatus.Active,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateStatus(status);
        int boundedLimit = ValidateLimit(limit);
        string term = query.Trim();
        if (term.Length == 0)
        {
            return await ListAsync(status, boundedLimit, cancellationToken).ConfigureAwait(false);
        }

        await using CloudScribeDbContext context = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DocumentEntity[] documents = await context.Documents
            .AsNoTracking()
            .Where(item => item.Status == (int)status
                && (item.Title.Contains(term) || item.DraftText.Contains(term)))
            .OrderByDescending(item => item.UpdatedAtUnixMilliseconds)
            .ThenBy(item => item.Title)
            .Take(boundedLimit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return documents.Select(ToSummary).ToArray();
    }

    public async Task<DocumentSnapshot> SaveAsync(
        DocumentSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDocumentId(request.DocumentId);
        string normalizedTitle = ValidateTitle(request.Title);
        ArgumentNullException.ThrowIfNull(request.Text);
        ValidateRevisionMetadata(request.RevisionName, request.ImportProvenance);
        if (request.ExpectedConcurrencyVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Expected concurrency version must be positive.");
        }

        await using CloudScribeDbContext context = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DocumentEntity document = await context.Documents
            .SingleOrDefaultAsync(item => item.Id == request.DocumentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Document {request.DocumentId:N} does not exist.");
        if (document.ConcurrencyVersion != request.ExpectedConcurrencyVersion)
        {
            throw new DocumentConcurrencyException(request.DocumentId, request.ExpectedConcurrencyVersion);
        }

        Guid revisionId = Guid.NewGuid();
        byte[] bytes = StrictUtf8.GetBytes(request.Text);
        DocumentContentCommit commit = await contentStore
            .CommitAsync(request.DocumentId, revisionId, bytes, cancellationToken)
            .ConfigureAwait(false);
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        try
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            document.Title = normalizedTitle;
            document.DraftText = request.Text;
            document.UpdatedAtUnixMilliseconds = now;
            document.CurrentRevisionId = revisionId;
            document.ConcurrencyVersion++;
            context.DocumentRevisions.Add(BuildRevision(
                request.DocumentId,
                revisionId,
                now,
                request.RevisionKind,
                request.RevisionName,
                request.Text,
                commit,
                request.ImportProvenance));

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new DocumentConcurrencyException(request.DocumentId, request.ExpectedConcurrencyVersion) { Source = exception.Source };
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(document, request.Text);
        }
        catch
        {
            await TryDeleteUnreferencedAsync(commit).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DocumentSummary> ChangeStatusAsync(
        Guid documentId,
        DocumentStatus status,
        long expectedConcurrencyVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        ValidateStatus(status);
        if (expectedConcurrencyVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedConcurrencyVersion));
        }

        await using CloudScribeDbContext context = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DocumentEntity document = await context.Documents
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Document {documentId:N} does not exist.");
        if (document.ConcurrencyVersion != expectedConcurrencyVersion)
        {
            throw new DocumentConcurrencyException(documentId, expectedConcurrencyVersion);
        }

        document.Status = (int)status;
        document.UpdatedAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        document.ConcurrencyVersion++;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DocumentConcurrencyException(documentId, expectedConcurrencyVersion);
        }

        return ToSummary(document);
    }

    private async Task<string> ReadRevisionTextAsync(
        DocumentRevisionEntity revision,
        CancellationToken cancellationToken)
    {
        byte[] databaseBytes = StrictUtf8.GetBytes(revision.ContentText);
        string databaseHash = Convert.ToHexString(SHA256.HashData(databaseBytes)).ToLowerInvariant();
        if (!string.Equals(databaseHash, revision.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The revision text stored in the database failed its SHA-256 integrity check.");
        }

        if (revision.ContentRelativePath is null || revision.ContentByteLength is null)
        {
            return revision.ContentText;
        }

        DocumentContentCommit commit = new(
            revision.ContentRelativePath,
            revision.ContentSha256,
            revision.ContentByteLength.Value);
        byte[] content = await contentStore.ReadVerifiedAsync(commit, cancellationToken).ConfigureAwait(false);
        string fileText = StrictUtf8.GetString(content);
        if (!string.Equals(fileText, revision.ContentText, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The immutable revision file and database revision text disagree.");
        }

        return fileText;
    }

    private async Task TryDeleteUnreferencedAsync(DocumentContentCommit commit)
    {
        try
        {
            await contentStore.DeleteCommittedAsync(commit).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static DocumentRevisionEntity BuildRevision(
        Guid documentId,
        Guid revisionId,
        long now,
        DocumentRevisionKind kind,
        string? name,
        string text,
        DocumentContentCommit commit,
        string? importProvenance) => new()
        {
            Id = revisionId,
            DocumentId = documentId,
            CreatedAtUnixMilliseconds = now,
            RevisionKind = (int)kind,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            ContentText = text,
            ContentSha256 = commit.Sha256,
            ContentRelativePath = commit.RelativePath,
            ContentByteLength = commit.ByteLength,
            ImportProvenance = string.IsNullOrWhiteSpace(importProvenance) ? null : importProvenance.Trim(),
        };

    private static DocumentSummary ToSummary(DocumentEntity document) => new(
        document.Id,
        document.Title,
        document.UpdatedAtUnixMilliseconds,
        (DocumentStatus)document.Status,
        document.IsFavorite,
        document.ConcurrencyVersion);

    private static DocumentSnapshot ToSnapshot(DocumentEntity document, string text) => new(
        document.Id,
        document.Title,
        text,
        document.CreatedAtUnixMilliseconds,
        document.UpdatedAtUnixMilliseconds,
        (DocumentStatus)document.Status,
        document.IsFavorite,
        document.CurrentRevisionId,
        document.VoiceReference,
        document.PresetReference,
        document.ConcurrencyVersion);

    private static string ValidateTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        string normalized = title.Trim();
        if (normalized.Length is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(title), "Document title must contain 1 to 240 characters.");
        }

        return normalized;
    }

    private static void ValidateRevisionMetadata(string? name, string? importProvenance)
    {
        if (name?.Trim().Length > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        if (importProvenance?.Trim().Length > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(importProvenance));
        }
    }

    private static void ValidateDocumentId(Guid documentId)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID is required.", nameof(documentId));
        }
    }

    private static void ValidateStatus(DocumentStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    private static int ValidateLimit(int limit)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Document query limit must be between 1 and 200.");
        }

        return limit;
    }
}
