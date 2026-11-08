using System.Security.Cryptography;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Persistence;

public sealed partial class EfNarrationCopyStore
{
    private async Task<DocumentSnapshot> ReadPinnedAsync(
        CloudScribeDbContext context, Guid documentId, Guid revisionId, string expectedHash,
        bool requireFile, CancellationToken cancellationToken)
    {
        RequireHash(expectedHash);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(
            row => row.Id == documentId, cancellationToken).ConfigureAwait(false);
        if (document is null || document.Status != (int)DocumentStatus.Active || document.CurrentRevisionId != revisionId)
            throw new InvalidOperationException("The pinned document is missing, inactive or changed; preparation was not reused.");
        var revision = await context.DocumentRevisions.AsNoTracking().SingleOrDefaultAsync(
            row => row.Id == revisionId && row.DocumentId == documentId, cancellationToken).ConfigureAwait(false);
        if (revision is null || !string.Equals(revision.ContentSha256, expectedHash, StringComparison.Ordinal)
            || !string.Equals(Hash(revision.ContentText), expectedHash, StringComparison.Ordinal)
            || !string.Equals(revision.ContentText, document.DraftText, StringComparison.Ordinal))
            throw new InvalidDataException("The pinned revision failed its database content integrity check.");
        if ((revision.ContentRelativePath is null) != (revision.ContentByteLength is null)
            || (requireFile && revision.ContentRelativePath is null))
            throw new InvalidDataException("The pinned revision has incomplete durable content metadata.");
        if (revision.ContentRelativePath is not null && revision.ContentByteLength is long length)
        {
            byte[] bytes = await contentStore.ReadVerifiedAsync(
                new(revision.ContentRelativePath, expectedHash, length), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(StrictUtf8.GetString(bytes), revision.ContentText, StringComparison.Ordinal))
                throw new InvalidDataException("The pinned revision file and database content disagree.");
        }
        return Snapshot(document);
    }

    private async Task<DocumentSnapshot> AddDocumentAsync(
        CloudScribeDbContext context, Guid documentId, Guid revisionId, string title, string text,
        string revisionName, string provenance, long now, CancellationToken cancellationToken)
    {
        if (await context.Documents.AnyAsync(row => row.Id == documentId, cancellationToken).ConfigureAwait(false)
            || await context.DocumentRevisions.AnyAsync(row => row.Id == revisionId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A durable document identity exists without its matching preparation pin.");
        // Blobs are flushed before the atomic database publication. A crash/rollback can leave an
        // unreferenced immutable blob; deterministic IDs safely reuse its verified bytes on retry.
        // Never delete here: a commit may have succeeded even if its acknowledgement was lost.
        DocumentContentCommit commit = await contentStore.CommitAsync(
            documentId, revisionId, StrictUtf8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        var document = new DocumentEntity
        {
            Id = documentId,
            CurrentRevisionId = revisionId,
            Title = title,
            DraftText = text,
            CreatedAtUnixMilliseconds = now,
            UpdatedAtUnixMilliseconds = now,
            Status = (int)DocumentStatus.Active,
            ConcurrencyVersion = 1,
        };
        context.Documents.Add(document);
        context.DocumentRevisions.Add(new DocumentRevisionEntity
        {
            Id = revisionId,
            DocumentId = documentId,
            ContentText = text,
            ContentSha256 = commit.Sha256,
            ContentRelativePath = commit.RelativePath,
            ContentByteLength = commit.ByteLength,
            CreatedAtUnixMilliseconds = now,
            RevisionKind = (int)DocumentRevisionKind.Import,
            Name = revisionName,
            ImportProvenance = provenance,
        });
        return Snapshot(document);
    }

    private static DocumentSnapshot Snapshot(DocumentEntity document) => new(
        document.Id, document.Title, document.DraftText, document.CreatedAtUnixMilliseconds,
        document.UpdatedAtUnixMilliseconds, (DocumentStatus)document.Status, document.IsFavorite,
        document.CurrentRevisionId, document.VoiceReference, document.PresetReference, document.ConcurrencyVersion);

    private static string NarrationTitle(string sourceTitle)
    {
        int length = Math.Min(sourceTitle.Length, 212);
        if (length < sourceTitle.Length && char.IsHighSurrogate(sourceTitle[length - 1]) && char.IsLowSurrogate(sourceTitle[length]))
            length--;
        return sourceTitle[..length] + " (narration, URLs skipped)";
    }
    private static string CopyKey(Guid sourceId, Guid revisionId, string hash, string version) =>
        $"narration-copy-v1/{sourceId:N}/{revisionId:N}/{hash}/{version}/{TransformSettings}";

    private static Guid StableId(string key) => new(SHA256.HashData(StrictUtf8.GetBytes(key)).AsSpan(0, 16));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(text)));

    private static void RequireIdentity(Guid identity)
    {
        if (identity == Guid.Empty)
            throw new ArgumentException("A non-empty document or batch identity is required.", nameof(identity));
    }

    private static void RequireHash(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character))
            || !string.Equals(hash, hash.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("A canonical lowercase SHA-256 hash is required.", nameof(hash));
    }
}
