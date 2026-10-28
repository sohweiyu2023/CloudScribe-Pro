using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    private static string TextHash(ReadOnlyMemory<byte> payload)
    {
        using var json = JsonDocument.Parse(payload);
        return ArticleAudioManifest.HashText(json.RootElement.GetProperty("input").GetProperty("text").GetString()!);
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var item in values) command.Parameters.AddWithValue(item.Name, item.Value ?? DBNull.Value);
        return command;
    }

    private static async Task<GoogleGenerationPersistedQueueState> LoadPreviousAsync(SqliteConnection connection,
        SqliteTransaction transaction, GoogleRecoveryIntent intent, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(intent.CompiledPayload);
        var root = payload.RootElement;
        string? text = root.GetProperty("input").GetProperty("text").GetString();
        if (root.GetProperty("input").EnumerateObject().Count() != 1
            || !string.Equals(root.GetProperty("voice").GetProperty("name").GetString(), intent.Envelope.VoiceName
, StringComparison.Ordinal) || !string.Equals(root.GetProperty("audioConfig").GetProperty("audioEncoding").GetString(), intent.Envelope.AudioEncoding, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery requires exact saved plain-text request controls.");
        using (var document = Command(connection, transaction,
            "SELECT DraftText, CurrentRevisionId, Status FROM documents WHERE lower(Id)=$document;", ("$document", intent.DocumentId.ToString("D"))))
        using (var row = await document.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false) || !string.Equals(row.GetString(0), text
, StringComparison.Ordinal) || !Guid.TryParse(row.GetString(1), out var revision) || revision != intent.RevisionId || row.GetInt32(2) != 0)
                throw new InvalidOperationException("Recovery document revision is missing or changed.");
        }
        using var command = Command(connection, transaction, """
            SELECT AccountId,OperationStableId,IdempotencyKey,UnresolvedSubmission,ProviderRequestId,
            SubmissionAttempted,LocalAttemptId,DiagnosticCode,PayloadSha256,PayloadBytes,OutcomeObservedAtUnixMilliseconds,ErrorStatus
            FROM google_generation_queue_states WHERE AccountId=$account AND OperationStableId=$operation AND IdempotencyKey=$previous;
            """, ("$account", intent.Envelope.AccountId), ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId), ("$previous", intent.PreviousIdempotencyKey));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Historical attempt is missing.");
        var state = new GoogleGenerationPersistedQueueState(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5) != 0,
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10), (GoogleErrorStatus)reader.GetInt32(11)).Validate();
        if (!state.SubmissionAttempted || string.Equals(state.DiagnosticCode, "google-accepted", StringComparison.Ordinal))
            throw new InvalidOperationException("Only an existing unaccepted attempted identity can be considered for an additional attempt.");
        if (state.PayloadSha256 is not null && !string.Equals(state.PayloadSha256, intent.Envelope.CompiledPayloadSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Historical payload differs; additional-attempt approval cannot silently change the narration.");
        return state;
    }

    private static async Task RequireHeadAndBudgetAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleRecoveryIntent intent, GoogleRecoveryBudgetReview budget, long maximum, string logical, CancellationToken cancellationToken)
    {
        using (var head = Command(connection, transaction,
            "SELECT ChildKey FROM google_recovery_attempts WHERE LogicalItemId=$logical ORDER BY rowid DESC LIMIT 1;", ("$logical", logical)))
        {
            var key = await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (key is string latest && !string.Equals(latest, intent.PreviousIdempotencyKey, StringComparison.Ordinal))
                throw new InvalidOperationException("Recovery must retain the latest attempt chain; a historical branch cannot be reopened.");
        }
        using (var pending = Command(connection, transaction,
            "SELECT COUNT(*) FROM google_recovery_attempts WHERE AccountId=$account AND ProjectId=$project AND Consumed=0;",
            ("$account", intent.Envelope.AccountId), ("$project", intent.ProjectId)))
            if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("An approved additional attempt is already pending; reconcile that exact identity first.");
        using var scope = Command(connection, transaction, """
            SELECT RetainedFloorMinorUnits,ProjectCeilingMinorUnits,AllocationRemainingMinorUnits FROM google_recovery_scopes
            WHERE AccountId=$account AND ProjectId=$project AND Currency=$currency AND Scale=$scale;
            """, ("$account", intent.Envelope.AccountId), ("$project", intent.ProjectId), ("$currency", intent.Currency), ("$scale", intent.Scale));
        using var row = await scope.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await row.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (budget.RetainedCommittedMinorUnits < row.GetInt64(0) || budget.ProjectCeilingMinorUnits != row.GetInt64(1))
                throw new InvalidOperationException("Recovery cannot lower retained holds or change the project cap.");
            if (budget.RemainingAllocationMinorUnits > row.GetInt64(2))
            {
                var t = budget.AllocationTransition;
                object?[] old = [intent.Envelope.AccountId, intent.ProjectId, intent.Currency, (long)intent.Scale,
                    row.GetInt64(0), row.GetInt64(1), row.GetInt64(2)];
                if (t is null || !IsDigest(t.SourceApprovalSha256) || !string.Equals(t.PriorScopeSha256, Digest(old)
, StringComparison.Ordinal) || t.NewRemainingMinorUnits != budget.RemainingAllocationMinorUnits || t.PriorAllocationCeilingMinorUnits < 0
                    || t.NewAllocationCeilingMinorUnits <= t.PriorAllocationCeilingMinorUnits
                    || t.NewAllocationCeilingMinorUnits - t.PriorAllocationCeilingMinorUnits != budget.RemainingAllocationMinorUnits - row.GetInt64(2))
                    throw new InvalidOperationException("An exact independently admitted allocation transition is required.");
            }
            if (maximum > budget.RemainingAllocationMinorUnits)
                throw new InvalidOperationException("Recovery exceeds the reviewed allocation.");
        }
    }

    private static async Task<string> FinancialDigestAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        List<object?[]> rows = [];
        foreach (string table in new[] { "google_generation_queue_states", "google_generation_spend_authorizations" })
        {
            using var command = Command(connection, transaction, "SELECT * FROM " + table + " ORDER BY rowid;");
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new object[reader.FieldCount]; reader.GetValues(values);
                rows.Add(values.Select(x => x is DBNull ? null : x).ToArray());
            }
        }
        return Digest(rows);
    }
}
