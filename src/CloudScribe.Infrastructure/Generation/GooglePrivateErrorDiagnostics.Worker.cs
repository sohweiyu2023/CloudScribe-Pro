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
            payloadHash, correlation, envelope.DetailsOmitted, envelope);
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
            outcome = saved ? GoogleExplanationRetentionState.SavedPrivately : GoogleExplanationRetentionState.ExpiredBeforeSave;
            if (sequence == Volatile.Read(ref _writeSequence)) Volatile.Write(ref _writeState, saved ? 2 : 7);
        }
        catch (Exception error) when (NonFatal(error)) { if (sequence == Volatile.Read(ref _writeSequence)) Volatile.Write(ref _writeState, 4); }
        finally
        {
            Volatile.Write(ref _writerBusy, 0);
            ObservePersistence(record, outcome);
        }
    }

    private void ObservePersistence(PrivateProviderErrorRecord record, GoogleExplanationRetentionState outcome)
    {
        if (logger is null || !CloudScribeLog.ObserveIsEnabled(logger, LogLevel.Information)) return;
        CloudScribeLog.Observe(() =>
        {
            int bytes = Encoding.UTF8.GetByteCount(record.Message);
            CloudScribeLog.GooglePrivateExplanationPersistence(logger,
                record.TransportCorrelation, record.PayloadSha256, outcome, bytes, record.Truncated, record.ProviderDetailsOmitted);
        });
    }

    public Task WaitForPendingWriteAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _pendingWrite).WaitAsync(cancellationToken);

    public IReadOnlyList<PrivateProviderErrorRecord> ReadRecords() => persistence.Read();

    public async Task<IReadOnlyList<PrivateProviderErrorRecord>> ReadRecordsAsync(CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<PrivateProviderErrorRecord>> read;
        lock (_gate)
        {
            if (_disposed) return [];
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
