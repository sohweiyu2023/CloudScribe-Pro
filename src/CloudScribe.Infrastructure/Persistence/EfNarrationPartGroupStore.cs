using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class EfNarrationPartGroupStore(IDbContextFactory<CloudScribeDbContext> factory, IDocumentLibrary library) : INarrationPartGroupStore, ILibraryPageSource<NarrationLibraryArticle>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<NarrationPartPreparation> BeginAsync(Guid parentId, Guid sourceRevisionId, string sourceSha256,
        IReadOnlyList<string> orderedPartHashes, CancellationToken cancellationToken = default)
    {
        try { return await BeginCoreAsync(parentId, sourceRevisionId, sourceSha256, orderedPartHashes, cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateException error) { throw new InvalidOperationException("Preparation storage conflicted; inspect existing parts before repeating. No original was replaced.", error); }
    }

    public async Task RegisterAsync(Guid groupId, IReadOnlyList<NarrationPartMember> parts, CancellationToken cancellationToken = default)
    {
        try { await RegisterCoreAsync(groupId, parts, cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateException error) { throw new InvalidOperationException("Group storage conflicted; all original documents and parts were retained. Refresh before repeating.", error); }
    }


    private async Task<NarrationPartPreparation> BeginCoreAsync(Guid parentId, Guid sourceRevisionId, string sourceSha256,
        IReadOnlyList<string> orderedPartHashes, CancellationToken cancellationToken = default)
    {
        if (parentId == Guid.Empty || sourceRevisionId == Guid.Empty || orderedPartHashes.Count is < 1 or > 100)
            throw new InvalidOperationException("Narration preparation requires a saved original and 1–100 explicit parts.");
        RequireHash(sourceSha256);
        foreach (string hash in orderedPartHashes) RequireHash(hash);
        string hashes = string.Join(',', orderedPartHashes);
        string partition = Hash(hashes);
        var parent = await library.OpenAsync(parentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original is missing; no parts were created.");
        if (parent.CurrentRevisionId != sourceRevisionId || !string.Equals(Hash(parent.Text), sourceSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The original changed; preview its parts again.");
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var previous = await context.Set<NarrationPartGroupEntity>().AsNoTracking().SingleOrDefaultAsync(group =>
            group.ParentDocumentId == parentId,
            cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.SourceRevisionId != sourceRevisionId || !string.Equals(previous.PartitionSha256, partition, StringComparison.Ordinal))
                throw new InvalidOperationException("This article already has another preparation. Existing originals and parts are retained; no second batch was created.");
            if (!previous.Complete)
                throw new InvalidOperationException("This preparation is incomplete. Its existing parts remain visible; inspect them before creating another batch.");
            var priorLinks = await context.Set<NarrationPartLinkEntity>().Where(link => link.GroupId == previous.Id)
                .OrderBy(link => link.Ordinal).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (priorLinks.Length != previous.ExpectedPartCount)
                throw new InvalidOperationException("Existing preparation is incomplete; no new parts were created.");
            List<string> priorText = [];
            foreach (var link in priorLinks)
            {
                var child = await library.OpenAsync(link.ChildDocumentId, cancellationToken).ConfigureAwait(false);
                if (child is null || child.CurrentRevisionId != link.ChildRevisionId || !string.Equals(Hash(child.Text), link.ChildSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Existing parts changed since preparation. Review them; they were not reused as verified parts.");
                priorText.Add(child.Text);
            }
            if (!string.Equals(string.Concat(priorText), parent.Text, StringComparison.Ordinal))
                throw new InvalidOperationException("Existing parts no longer match the original; nothing was regenerated.");
            return new(previous.Id, true, previous.ExpectedPartCount);
        }
        if (await context.Set<NarrationPartLinkEntity>().AnyAsync(link => link.ChildDocumentId == parentId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A technical part cannot become a nested article group.");
        var entry = new NarrationPartGroupEntity
        {
            Id = Guid.NewGuid(),
            ParentDocumentId = parentId,
            SourceRevisionId = sourceRevisionId,
            SourceSha256 = sourceSha256,
            PartitionSha256 = partition,
            OrderedPartHashes = hashes,
            ExpectedPartCount = orderedPartHashes.Count,
            Complete = false,
        };
        context.Add(entry);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(entry.Id, false, entry.ExpectedPartCount);
    }

    private async Task RegisterCoreAsync(Guid groupId, IReadOnlyList<NarrationPartMember> parts, CancellationToken cancellationToken = default)
    {
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var group = await context.Set<NarrationPartGroupEntity>().SingleOrDefaultAsync(item => item.Id == groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A durable local preparation must exist before parts are registered.");
        if (parts.Count != group.ExpectedPartCount || parts.Select(part => part.DocumentId).Distinct().Count() != parts.Count
            || parts.Any(part => part.DocumentId == Guid.Empty || part.DocumentId == group.ParentDocumentId || part.RevisionId == Guid.Empty))
            throw new InvalidOperationException("Part identities/count are missing, repeated or self-referential.");
        foreach (var part in parts) RequireHash(part.Sha256);
        if (!string.Equals(string.Join(',', parts.Select(part => part.Sha256)), group.OrderedPartHashes, StringComparison.Ordinal))
            throw new InvalidOperationException("The ordered part hashes differ from the previewed preparation.");
        var parent = await library.OpenAsync(group.ParentDocumentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Original missing; all parts remain ordinary visible documents.");
        if (parent.CurrentRevisionId != group.SourceRevisionId || !string.Equals(Hash(parent.Text), group.SourceSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Original changed since preparation; no group was committed.");
        List<DocumentSnapshot> children = [];
        foreach (var part in parts)
        {
            var child = await library.OpenAsync(part.DocumentId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A part is missing; no group was committed.");
            if (child.CurrentRevisionId != part.RevisionId || !string.Equals(Hash(child.Text), part.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("A part changed since preparation; no group was committed.");
            children.Add(child);
        }
        if (!string.Equals(string.Concat(children.Select(child => child.Text)), parent.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("Parts must concatenate to the exact original, without normalization or omitted text.");
        if (group.Complete)
        {
            var existing = await context.Set<NarrationPartLinkEntity>().Where(link => link.GroupId == groupId)
                .OrderBy(link => link.Ordinal).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Length == parts.Count && existing.Select(link => new NarrationPartMember(link.ChildDocumentId, link.ChildRevisionId, link.ChildSha256)).SequenceEqual(parts))
                return;
            throw new InvalidOperationException("A completed group's provenance cannot be replaced.");
        }
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid[] ids = children.Select(child => child.Id).Append(parent.Id).ToArray();
        var current = await context.Documents.AsNoTracking().Where(document => ids.Contains(document.Id)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in children.Append(parent))
        {
            var row = current.SingleOrDefault(document => document.Id == snapshot.Id);
            if (row is null || row.CurrentRevisionId != snapshot.CurrentRevisionId || row.ConcurrencyVersion != snapshot.ConcurrencyVersion)
                throw new InvalidOperationException("Documents changed during provenance verification; no group was committed.");
        }
        if (await context.Set<NarrationPartLinkEntity>().AnyAsync(link => ids.Contains(link.ChildDocumentId), cancellationToken).ConfigureAwait(false)
            || await context.Set<NarrationPartGroupEntity>().AnyAsync(other => ids.Contains(other.ParentDocumentId) && other.ParentDocumentId != parent.Id, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A child already belongs to another group or is itself an article parent.");
        for (int index = 0; index < parts.Count; index++)
            context.Add(new NarrationPartLinkEntity
            {
                GroupId = groupId,
                ChildDocumentId = parts[index].DocumentId,
                ChildRevisionId = parts[index].RevisionId,
                ChildSha256 = parts[index].Sha256,
                Ordinal = index + 1,
            });
        group.Complete = true;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NarrationLibraryArticle>> QueryLibraryAsync(string query, int limit = 200, CancellationToken cancellationToken = default)
        => (await QueryPageAsync(query, 0, limit, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<LibraryPage<NarrationLibraryArticle>> QueryPageAsync(string query, int offset = 0, int limit = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var documents = context.Documents.AsNoTracking();
        var groups = context.Set<NarrationPartGroupEntity>().AsNoTracking().Where(group => group.Complete);
        var links = context.Set<NarrationPartLinkEntity>().AsNoTracking();
        string term = query.Trim();
        var matchingRoots = documents.Where(document => document.Status == (int)DocumentStatus.Active
            && !links.Any(link => link.ChildDocumentId == document.Id && groups.Any(group => group.Id == link.GroupId
                && documents.Any(parent => parent.Id == group.ParentDocumentId && parent.Status == (int)DocumentStatus.Active))))
            .Where(document => term.Length == 0 || document.Title.Contains(term) || document.DraftText.Contains(term)
                || groups.Any(group => group.ParentDocumentId == document.Id && links.Any(link => link.GroupId == group.Id
                    && documents.Any(child => child.Id == link.ChildDocumentId && (child.Title.Contains(term) || child.DraftText.Contains(term))))));
        int total = await matchingRoots.CountAsync(cancellationToken).ConfigureAwait(false);
        int currentOffset = total == 0 ? 0 : Math.Min(offset, (total - 1) / limit * limit);
        var roots = await matchingRoots.OrderByDescending(document => document.UpdatedAtUnixMilliseconds).ThenBy(document => document.Title).ThenBy(document => document.Id)
            .Skip(currentOffset).Take(limit).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Guid[] rootIds = roots.Select(document => document.Id).ToArray();
        var selectedGroups = await groups.Where(group => rootIds.Contains(group.ParentDocumentId)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Guid[] groupIds = selectedGroups.Select(group => group.Id).ToArray();
        var selectedLinks = await links.Where(link => groupIds.Contains(link.GroupId)).OrderBy(link => link.Ordinal).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Guid[] childIds = selectedLinks.Select(link => link.ChildDocumentId).ToArray();
        var children = await documents.Where(document => childIds.Contains(document.Id)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var articles = roots.Select(root =>
        {
            var batches = selectedGroups.Where(group => group.ParentDocumentId == root.Id).ToArray();
            Guid[] batchIds = batches.Select(group => group.Id).ToArray();
            var parts = selectedLinks.Where(link => batchIds.Contains(link.GroupId)).Select(link =>
            {
                var child = children.Single(document => document.Id == link.ChildDocumentId);
                return new NarrationLibraryPart(Summary(child), link.Ordinal, child.CurrentRevisionId != link.ChildRevisionId);
            }).ToArray();
            return new NarrationLibraryArticle(Summary(root), parts, batches.Any(group => root.CurrentRevisionId != group.SourceRevisionId)
                || parts.Any(part => part.ChangedSincePreparation));
        }).ToArray();
        return new(articles, total, currentOffset, limit);
    }

    private static DocumentSummary Summary(DocumentEntity document) => new(document.Id, document.Title,
        document.UpdatedAtUnixMilliseconds, (DocumentStatus)document.Status, document.IsFavorite, document.ConcurrencyVersion);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(value)));
    private static void RequireHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character)) || !string.Equals(hash, hash.ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidOperationException("Provenance requires canonical lowercase SHA256 hashes.");
    }
}
