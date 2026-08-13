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
            using CloudScribeDbContext context = await dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            DocumentEntity document = BuildNewDocument(documentId, revisionId, normalizedTitle, text, now);
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
        using CloudScribeDbContext context = await dbContextFactory
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

        string text = await ReadCurrentTextAsync(context, document, cancellationToken).ConfigureAwait(false);
        return ToSnapshot(document, text);
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(
        DocumentStatus status = DocumentStatus.Active,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateStatus(status);
        int boundedLimit = ValidateLimit(limit);
        using CloudScribeDbContext context = await dbContextFactory
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

        using CloudScribeDbContext context = await dbContextFactory
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
        ArgumentOutOfRangeException.ThrowIfLessThan(request.ExpectedConcurrencyVersion, 1);

        using CloudScribeDbContext context = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DocumentEntity document = await GetDocumentForWriteAsync(
            context,
            request.DocumentId,
            cancellationToken).ConfigureAwait(false);
        EnsureExpectedVersion(document, request.ExpectedConcurrencyVersion);

        return await SaveRevisionAsync(
            context,
            document,
            request,
            normalizedTitle,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentSummary> ChangeStatusAsync(
        Guid documentId,
        DocumentStatus status,
        long expectedConcurrencyVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        ValidateStatus(status);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedConcurrencyVersion, 1);

        using CloudScribeDbContext context = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DocumentEntity document = await GetDocumentForWriteAsync(
            context,
            documentId,
            cancellationToken).ConfigureAwait(false);
        EnsureExpectedVersion(document, expectedConcurrencyVersion);

        document.Status = (int)status;
        document.UpdatedAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        document.ConcurrencyVersion++;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new DocumentConcurrencyException(documentId, expectedConcurrencyVersion, exception);
        }

        return ToSummary(document);
    }

    private async Task<DocumentSnapshot> SaveRevisionAsync(
        CloudScribeDbContext context,
        DocumentEntity document,
        DocumentSaveRequest request,
        string normalizedTitle,
        CancellationToken cancellationToken)
    {
        Guid revisionId = Guid.NewGuid();
        byte[] bytes = StrictUtf8.GetBytes(request.Text);
        DocumentContentCommit commit = await contentStore
            .CommitAsync(request.DocumentId, revisionId, bytes, cancellationToken)
            .ConfigureAwait(false);
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        try
        {
            using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            ApplyDocumentSave(document, request, normalizedTitle, revisionId, now);
            context.DocumentRevisions.Add(BuildRevision(
                request.DocumentId,
                revisionId,
                now,
                request.RevisionKind,
                request.RevisionName,
                request.Text,
                commit,
                request.ImportProvenance));
            await SaveChangesWithConcurrencyTranslationAsync(
                context,
                request.DocumentId,
                request.ExpectedConcurrencyVersion,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(document, request.Text);
        }
        catch
        {
            await TryDeleteUnreferencedAsync(commit).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> ReadCurrentTextAsync(
        CloudScribeDbContext context,
        DocumentEntity document,
        CancellationToken cancellationToken)
    {
        if (document.CurrentRevisionId is not Guid revisionId)
        {
            return document.DraftText;
        }

        DocumentRevisionEntity? revision = await context.DocumentRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == revisionId && item.DocumentId == document.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
        {
            throw new InvalidDataException("The document references a revision that does not exist.");
        }

        string text = await ReadRevisionTextAsync(revision, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(text, document.DraftText, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The durable revision and current document draft disagree.");
        }

        return text;
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

    private static async Task<DocumentEntity> GetDocumentForWriteAsync(
        CloudScribeDbContext context,
        Guid documentId,
        CancellationToken cancellationToken) =>
        await context.Documents
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Document {documentId:N} does not exist.");

    private static async Task SaveChangesWithConcurrencyTranslationAsync(
        CloudScribeDbContext context,
        Guid documentId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new DocumentConcurrencyException(documentId, expectedVersion, exception);
        }
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

    private static DocumentEntity BuildNewDocument(
        Guid documentId,
        Guid revisionId,
        string title,
        string text,
        long now) => new()
        {
            Id = documentId,
            Title = title,
            DraftText = text,
            CreatedAtUnixMilliseconds = now,
            UpdatedAtUnixMilliseconds = now,
            Status = (int)DocumentStatus.Active,
            IsFavorite = false,
            CurrentRevisionId = revisionId,
            ConcurrencyVersion = 1,
        };

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

    private static void ApplyDocumentSave(
        DocumentEntity document,
        DocumentSaveRequest request,
        string normalizedTitle,
        Guid revisionId,
        long now)
    {
        document.Title = normalizedTitle;
        document.DraftText = request.Text;
        document.UpdatedAtUnixMilliseconds = now;
        document.CurrentRevisionId = revisionId;
        document.ConcurrencyVersion++;
    }

    private static void EnsureExpectedVersion(DocumentEntity document, long expectedVersion)
    {
        if (document.ConcurrencyVersion != expectedVersion)
        {
            throw new DocumentConcurrencyException(document.Id, expectedVersion);
        }
    }

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
            throw new ArgumentException("Document title must contain 1 to 240 characters.", nameof(title));
        }

        return normalized;
    }

    private static void ValidateRevisionMetadata(string? name, string? importProvenance)
    {
        if (name?.Trim().Length > 240)
        {
            throw new ArgumentException("Revision name cannot exceed 240 characters.", nameof(name));
        }

        if (importProvenance?.Trim().Length > 2048)
        {
            throw new ArgumentException("Import provenance cannot exceed 2048 characters.", nameof(importProvenance));
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
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        return limit;
    }
}
