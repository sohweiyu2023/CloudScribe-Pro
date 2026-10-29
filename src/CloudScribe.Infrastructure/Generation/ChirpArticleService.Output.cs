using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private int _outputInFlight;
    private PendingAcceptedMedia? _pendingAcceptedMedia; // At most one owned response, bounded to 256 MiB, retained on local IO failure.
    private string MediaRoot => Path.Combine(paths.RootDirectory, "accepted-chirp-article-audio");

    public async Task<string> PersistAcceptedAsync(GenerationProviderRequest request, GoogleGenerationQueueOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.RequiresReconciliation || outcome.Response is not { IsAccepted: true } response
            || !string.Equals(response.DiagnosticCode, "google-accepted", StringComparison.Ordinal)
            || !string.Equals(response.MediaContentType, "audio/mpeg", StringComparison.OrdinalIgnoreCase)
            || response.MediaBytes.Length is < 12 or > 256 * 1024 * 1024 || request.CompiledPayload.Length is < 1 or > 5_000
            || request.IdempotencyKey.Length > 128 || !request.IdempotencyKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Only a verified terminal accepted MP3 response can become internal article output.");
        if (Interlocked.CompareExchange(ref _outputInFlight, 1, 0) != 0) throw new InvalidOperationException("A local article output operation is already running.");
        try
        {
            // Dispatch cancellation cannot truncate already accepted audio. This bounded local completion sends nothing.
            _ = cancellationToken;
            if (_pendingAcceptedMedia is { } pending && (!string.Equals(pending.Request.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
                || !string.Equals(pending.Request.AccountId, request.AccountId, StringComparison.Ordinal)
                || !pending.Request.CompiledPayload.Span.SequenceEqual(request.CompiledPayload.Span)
                || !pending.Media.AsSpan().SequenceEqual(response.MediaBytes.Span)))
                throw new InvalidOperationException("Recover the existing accepted local output before storing a different response.");
            _pendingAcceptedMedia ??= new(new(request.ProviderStableId, request.OperationStableId, request.AccountId,
                request.IdempotencyKey, request.CompiledPayload.ToArray(), request.OutputFormat), response.MediaBytes.ToArray());
            return await PersistOwnedMediaAsync(_pendingAcceptedMedia).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _outputInFlight, 0); }
    }

    private async Task<string> PersistOwnedMediaAsync(PendingAcceptedMedia pending)
    {
        using var completion = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var journal = await BeginOutputJournalAsync(pending.Request, pending.Media, completion.Token).ConfigureAwait(false);
        await SaveMediaAsync(journal, pending.Media, completion.Token).ConfigureAwait(false);
        await FinishOutputJournalAsync(journal, completion.Token).ConfigureAwait(false);
        _pendingAcceptedMedia = null;
        return journal.Receipt.Audio.AudioPath;
    }

    private async Task<OutputJournal> BeginOutputJournalAsync(GenerationProviderRequest request, ReadOnlyMemory<byte> media, CancellationToken token)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT AuthorizationId,PlanSha256,CoverageOrdinal,ClaimedLocalAttemptId,EnvelopeJson FROM chirp_article_attempts
            WHERE ChildKey=$key AND AccountId=$account AND Consumed=1;
            """, ("$key", request.IdempotencyKey), ("$account", request.AccountId));
        string id, plan, attempt; int ordinal; GoogleGenerationSubmissionEnvelope envelope;
        using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("No owned consumed internal request exists for this accepted output.");
            id = row.GetString(0); plan = row.GetString(1); ordinal = row.GetInt32(2); attempt = row.GetString(3);
            envelope = ParseStored<GoogleGenerationSubmissionEnvelope>(row.GetString(4));
        }
        var stored = await StoredAsync(connection, transaction, plan, token).ConfigureAwait(false);
        var part = stored.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal && x.Range.ReusedReceiptSha256 is null);
        if (!string.Equals(request.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal)
            || !string.Equals(request.OperationStableId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            || !string.Equals(request.AccountId, stored.AccountId, StringComparison.Ordinal)
            || envelope.ChirpAuthorizationId != Guid.ParseExact(id, "N") || envelope.RecoveryAuthorizationId is not null
            || !string.Equals(ChirpPolicyFiles.Hash(request.CompiledPayload.Span), part.PayloadSha256, StringComparison.Ordinal)
            || request.CompiledPayload.Length != part.PayloadBytes || !string.Equals(envelope.CompiledPayloadSha256, part.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Accepted output does not belong to the exact approved internal request.");
        string destination = Path.Combine(MediaRoot, id + ".mp3");
        var audio = new ArticleAcceptedAudio(stored.Article.Proposal.DocumentId, stored.Article.Proposal.RevisionId,
            part.Range.TextSha256, request.AccountId, request.OperationStableId, request.IdempotencyKey, attempt,
            part.PayloadSha256!, destination, media.Length, ChirpPolicyFiles.Hash(media.Span), stored.Article.Proposal.VoiceName, stored.Article.Proposal.LanguageCode);
        var receipt = new ChirpSegmentAcceptedAudio(plan, ordinal, part.Range, audio);
        ChirpArticleEvidenceResolver.RequireAcceptedQueue(audio, await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false));
        string json = JsonSerializer.Serialize(receipt), hash = ChirpPolicyValidation.Hash(receipt);
        if (json.Length > 32768) throw new InvalidDataException("Accepted internal provenance exceeds its size bound.");
        using (var insert = ChirpArticleSql.Command(connection, transaction,
            "INSERT INTO chirp_article_output_journals VALUES($id,$json,$hash) ON CONFLICT(AuthorizationId) DO NOTHING;", ("$id", id), ("$json", json), ("$hash", hash)))
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var journal = await ReadJournalAsync(connection, transaction, id, token).ConfigureAwait(false);
        if (journal.Receipt != receipt) throw new InvalidOperationException("An accepted output journal already binds different bytes or provenance.");
        token.ThrowIfCancellationRequested(); transaction.Commit(); return journal;
    }

    private async Task SaveMediaAsync(OutputJournal journal, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        string destination = journal.Receipt.Audio.AudioPath;
        ChirpPolicyFiles.RejectLinks(destination); Directory.CreateDirectory(MediaRoot);
        if (!File.Exists(destination))
        {
            string pending = destination + ".pending";
            ChirpPolicyFiles.RejectLinks(pending);
            if (File.Exists(pending))
            {
                try { await VerifyMediaAsync(pending, journal.Receipt.Audio, token).ConfigureAwait(false); }
                catch (InvalidOperationException exception)
                {
                    // Only an owned full accepted response can repair a partial local write. Preserve the partial bytes too.
                    if (Directory.GetFiles(MediaRoot, Path.GetFileName(destination) + ".partial-*").Length >= 2)
                        throw new InvalidOperationException("Repeated local write failures require storage review; accepted bytes remain retained in this session.", exception);
                    File.Move(pending, destination + ".partial-" + Guid.NewGuid().ToString("N"), overwrite: false);
                }
            }
            if (!File.Exists(pending))
            {
                using var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await file.WriteAsync(bytes, token).ConfigureAwait(false); file.Flush(flushToDisk: true);
            }
            await VerifyMediaAsync(pending, journal.Receipt.Audio, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(pending, destination, overwrite: false);
        }
        await VerifyMediaAsync(destination, journal.Receipt.Audio, token).ConfigureAwait(false);
    }

    private static async Task VerifyMediaAsync(string path, ArticleAcceptedAudio audio, CancellationToken token)
    {
        byte[] bytes = await ChirpPolicyFiles.ReadAsync(path, 256 * 1024 * 1024, token).ConfigureAwait(false);
        if (bytes.LongLength != audio.AudioBytes || !string.Equals(ChirpPolicyFiles.Hash(bytes), audio.AudioSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Accepted local audio is incomplete or changed. Preserve it; do not regenerate automatically.");
    }

    private sealed record OutputJournal(Guid AuthorizationId, ChirpSegmentAcceptedAudio Receipt);
    private sealed record PendingAcceptedMedia(GenerationProviderRequest Request, byte[] Media);
}
