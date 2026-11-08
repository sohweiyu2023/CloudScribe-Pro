using CloudScribe.Application.Documents;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private readonly INarrationCopyStore _narrationCopies = narrationCopies
        ?? new EfNarrationCopyStore(factory, new DocumentContentStore(paths), clock);

    private async Task<ChirpArticleCurrentEvidence> CurrentFreshArticleAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, ChirpRequestIntent intent, CancellationToken token)
    {
        await RequireCurrentNarrationCopyAsync(intent.DocumentId, token).ConfigureAwait(false);
        return await CurrentAsync(connection, transaction, policy, intent, token).ConfigureAwait(false);
    }

    // Fresh authority checks use durable source and child pins. Historical readback and exact
    // admitted recovery deliberately continue to use their immutable approved child payload.
    private async Task RequireCurrentNarrationCopyAsync(Guid documentId, CancellationToken token)
    {
        try { _ = await _narrationCopies.FindAsync(documentId, token).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            BatchDiagnostics.Source(logger, null, documentId, BatchSourceOutcome.NarrationInvalidated);
            throw;
        }
    }
}
