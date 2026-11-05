using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GooglePrivateErrorDiagnostics
{
    private int _writerBusy;
    private Task _pendingWrite = Task.CompletedTask;
    private Task<IReadOnlyList<PrivateProviderErrorRecord>>? _pendingRead;
    private int _writeState;
    private long _writeSequence;
    private int _readStatus;
    private PrivateArticlePersistenceEvidence? _lastArticlePersistence;
    public PrivateProviderErrorReadStatus ReadStatus => (PrivateProviderErrorReadStatus)Volatile.Read(ref _readStatus);
    public GoogleExplanationRetentionState PersistenceOutcome => Volatile.Read(ref _writeState) switch
    {
        1 => GoogleExplanationRetentionState.PrivateWritePending,
        2 => GoogleExplanationRetentionState.SavedPrivately,
        3 => GoogleExplanationRetentionState.StorageBusy,
        4 => GoogleExplanationRetentionState.StorageFailed,
        5 => GoogleExplanationRetentionState.IneligibleOrUnparseable,
        6 => GoogleExplanationRetentionState.ExpiredOrChanged,
        7 => GoogleExplanationRetentionState.ExpiredBeforeSave,
        _ => GoogleExplanationRetentionState.NoActivePrivateTicket
    };
    public string PersistenceStatus => Volatile.Read(ref _writeState) switch
    {
        1 => "Private record save pending; durability is not yet verified.",
        2 => "Private record saved locally.",
        3 => "Private storage worker was busy; this diagnostic was not retained.",
        4 => "Private record could not be saved. Normal request outcome is unchanged.",
        5 => "Response had no eligible bounded validation message; no private text retained.",
        6 => "Diagnostic opt-in expired or request identity changed; no private text retained.",
        7 => "Private record expired before it could be saved.",
        _ => "No private record has been captured in this session.",
    };

    private GoogleExplanationRetentionState QueueWrite(PrivateProviderExplanationText explanation, string payloadHash, string correlation)
        => QueueRecord(BoundRecord(explanation, payloadHash, correlation));

    private GoogleExplanationRetentionState QueueEnvelope(PrivateProviderResponseEnvelope envelope, string payloadHash, string correlation)
    {
        var record = new PrivateProviderErrorRecord(Guid.NewGuid(), clock.GetUtcNow(),
            "Private response evidence; inspect completeness and redaction flags before interpretation.", envelope.Truncated,
            payloadHash, correlation, envelope.DetailsOmitted, envelope, _activeArticleJob, _activeArticleOrdinal);
        while (JsonSerializer.SerializeToUtf8Bytes(record).Length > PrivateRecordProtection.MaximumPlaintextBytes)
        {
            envelope = envelope with
            {
                RedactedBody = PrivateProviderEnvelopeRedactor.LimitUtf8(envelope.RedactedBody,
                Encoding.UTF8.GetByteCount(envelope.RedactedBody) * 3 / 4),
                Truncated = true
            };
            record = record with { Response = envelope, Truncated = true };
        }
        return QueueRecord(record);
    }

    private GoogleExplanationRetentionState QueueRecord(PrivateProviderErrorRecord record)
    {
        long sequence = Interlocked.Increment(ref _writeSequence);
        if (Interlocked.CompareExchange(ref _writerBusy, 1, 0) != 0)
        { Volatile.Write(ref _writeState, 3); return GoogleExplanationRetentionState.StorageBusy; } // Never backpressure a provider outcome.
        try
        {
            Volatile.Write(ref _writeState, 1);
            using (ExecutionContext.SuppressFlow())
                _pendingWrite = Task.Run(() => PersistRedactedRecord(record, sequence)); // Closure has only bounded redacted record, sequence and owner.
            return GoogleExplanationRetentionState.PrivateWritePending;
        }
        catch { Volatile.Write(ref _writerBusy, 0); throw; }
    }

    private void PersistRedactedRecord(PrivateProviderErrorRecord record, long sequence)
    {
        var outcome = GoogleExplanationRetentionState.StorageFailed;
        try
        {
            bool saved = persistence.Save(record);
            bool verified = saved && VerifySavedRecord(record);
            outcome = verified ? GoogleExplanationRetentionState.SavedPrivately : saved
                ? GoogleExplanationRetentionState.StorageFailed : GoogleExplanationRetentionState.ExpiredBeforeSave;
            if (sequence == Volatile.Read(ref _writeSequence)) Volatile.Write(ref _writeState, verified ? 2 : saved ? 4 : 7);
        }
        catch (Exception error) when (NonFatal(error)) { if (sequence == Volatile.Read(ref _writeSequence)) Volatile.Write(ref _writeState, 4); }
        finally
        {
            Volatile.Write(ref _lastArticlePersistence, new(record.ArticleJobId, record.ArticleOrdinal, record.PayloadSha256,
                outcome == GoogleExplanationRetentionState.SavedPrivately,
                outcome == GoogleExplanationRetentionState.SavedPrivately && !record.Truncated
                    && record.Response is { HttpStatus: 400, ReadCompleted: true, Truncated: false, ParseState: "Json" }));
            Volatile.Write(ref _writerBusy, 0);
            ObservePersistence(record, outcome);
        }
    }

    private bool VerifySavedRecord(PrivateProviderErrorRecord record)
    {
        try
        {
            var read = persistence.ReadWithStatus(record.Id);
            Volatile.Write(ref _readStatus, (int)read.Status);
            if (read.Status != PrivateProviderErrorReadStatus.Success) return false;
            var saved = read.Records.SingleOrDefault(x => x.Id == record.Id);
            if (saved is not null && string.Equals(saved.PayloadSha256, record.PayloadSha256, StringComparison.Ordinal)
                && string.Equals(saved.TransportCorrelation, record.TransportCorrelation, StringComparison.Ordinal)
                && SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(saved)).AsSpan()
                    .SequenceEqual(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record)))) return true;
        }
        catch (Exception error) when (NonFatal(error)) { }
        Volatile.Write(ref _readStatus, (int)PrivateProviderErrorReadStatus.Failed);
        return false;
    }

    private void ObservePersistence(PrivateProviderErrorRecord record, GoogleExplanationRetentionState outcome)
    {
        if (logger is null || !CloudScribeLog.ObserveIsEnabled(logger, LogLevel.Information)) return;
        CloudScribeLog.Observe(() =>
        {
            int bytes = Encoding.UTF8.GetByteCount(record.Response?.RedactedBody ?? record.Message);
            int recordBytes = JsonSerializer.SerializeToUtf8Bytes(record).Length;
            CloudScribeLog.GooglePrivateExplanationPersistence(logger,
                record.TransportCorrelation, record.PayloadSha256, outcome, record.Response is not null, bytes, recordBytes,
                record.Truncated, record.ProviderDetailsOmitted);
        });
    }

    public Task WaitForPendingWriteAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _pendingWrite).WaitAsync(cancellationToken);

    public IReadOnlyList<PrivateProviderErrorRecord> ReadRecords()
    {
        try
        {
            var result = persistence.ReadWithStatus();
            Volatile.Write(ref _readStatus, (int)result.Status);
            return result.Status == PrivateProviderErrorReadStatus.Success ? result.Records : [];
        }
        catch (Exception error) when (NonFatal(error))
        { Volatile.Write(ref _readStatus, (int)PrivateProviderErrorReadStatus.Failed); return []; }
    }

    public async Task<IReadOnlyList<PrivateProviderErrorRecord>> ReadRecordsAsync(CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<PrivateProviderErrorRecord>> read;
        lock (_gate)
        {
            if (_disposed) { Volatile.Write(ref _readStatus, (int)PrivateProviderErrorReadStatus.Disposed); return []; }
            if (_pendingRead is null || _pendingRead.IsCompleted)
                using (ExecutionContext.SuppressFlow()) _pendingRead = Task.Run(ReadRecords);
            read = _pendingRead;
        }
        return await read.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private PrivateProviderErrorRecord BoundRecord(PrivateProviderExplanationText explanation, string payloadHash, string correlation)
    {
        string message = explanation.Message;
        string bounded = TruncatePrivateText(message, 2048);
        var record = new PrivateProviderErrorRecord(Guid.NewGuid(), clock.GetUtcNow(), bounded, explanation.Truncated || bounded.Length != message.Length,
            payloadHash, correlation, explanation.DetailsOmitted);
        while (JsonSerializer.SerializeToUtf8Bytes(record).Length > 4096)
        {
            bounded = TruncatePrivateText(bounded, Encoding.UTF8.GetByteCount(bounded) * 3 / 4);
            record = record with { Message = bounded, Truncated = true };
        }
        return record;
    }

    private static string TruncatePrivateText(string text, int bytes)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes < rune.Utf8SequenceLength) break;
            bytes -= rune.Utf8SequenceLength; result.Append(rune);
        }
        return result.ToString();
    }
}
