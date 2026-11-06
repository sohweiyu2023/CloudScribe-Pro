using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact-object job opt-in adapter over the existing protected private store and redactor.</summary>
public sealed class GoogleLongAudioProtectedResponseObserver(IPrivateProviderErrorPersistence persistence, TimeProvider clock)
    : IGoogleLongAudioResponseObserver
{
    private readonly System.Threading.Lock _gate = new();
    private GoogleLongAudioRequest? _request;
    private DateTimeOffset? _notAfter;
    private long _started;
    private TimeSpan? _lifetime;
    private Guid? _ticket;
    private int _writerBusy;

    public void Arm(GoogleLongAudioRequest request, bool explicitlyConfirmed, DateTimeOffset captureNotAfter)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset now = clock.GetUtcNow();
        if (!explicitlyConfirmed || captureNotAfter <= now)
            throw new InvalidOperationException("Exact bounded Long Audio capture consent and its existing expiry are required.");
        Prepare(request, captureNotAfter, captureNotAfter - now);
    }

    /// <summary>One exact response in an explicitly confirmed job scope, without an arbitrary run timer.
    /// This does not change private-record retention or authorize synthesis; each later response needs preparation again.</summary>
    public void ArmForJob(GoogleLongAudioRequest request, bool explicitlyConfirmed)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!explicitlyConfirmed) throw new InvalidOperationException("Exact Long Audio job capture consent is required.");
        Prepare(request, null, null);
    }

    private void Prepare(GoogleLongAudioRequest request, DateTimeOffset? notAfter, TimeSpan? lifetime)
    {
        lock (_gate)
        {
            if (_ticket.HasValue || Volatile.Read(ref _writerBusy) != 0 || !persistence.PrepareProtectedCapture())
                throw new InvalidOperationException("Protected Long Audio response storage is not ready.");
            _request = request; _ticket = null; _notAfter = notAfter; _started = clock.GetTimestamp(); _lifetime = lifetime;
        }
    }

    public void Disarm() { lock (_gate) { _request = null; _ticket = null; } }
    public bool IsReadyFor(GoogleLongAudioRequest request) { lock (_gate) return !_ticket.HasValue && Current(request); }
    public Guid Begin(GoogleLongAudioRequest request)
    {
        lock (_gate)
        {
            if (_ticket.HasValue || !Current(request)) throw new InvalidOperationException("Protected Long Audio capture is not armed for this exact request.");
            _ticket = Guid.NewGuid(); return _ticket.Value;
        }
    }
    public bool IsActiveFor(Guid ticket, GoogleLongAudioRequest request) { lock (_gate) return _ticket == ticket && Current(request); }
    public void FinishResponse(Guid ticket) { lock (_gate) { if (_ticket == ticket) { _ticket = null; _request = null; } } }
    private bool Current(GoogleLongAudioRequest request) => ReferenceEquals(_request, request)
        && (!_notAfter.HasValue || clock.GetUtcNow() < _notAfter.Value)
        && (!_lifetime.HasValue || clock.GetElapsedTime(_started) < _lifetime.Value) && Volatile.Read(ref _writerBusy) == 0;

    public Task<GoogleLongAudioObservation> ObserveAsync(Guid ticket, GoogleLongAudioRequest request, int httpStatus,
        ReadOnlyMemory<byte> boundedBody, GoogleResponseReadState readState, string transientAccessToken, bool operationError) =>
        ObserveCoreAsync(ticket, request, httpStatus, boundedBody, readState, transientAccessToken, operationError, false);

    public Task<GoogleLongAudioObservation> ObserveStorageMetadataAsync(Guid ticket, GoogleLongAudioRequest request, int httpStatus,
        ReadOnlyMemory<byte> boundedBody, GoogleResponseReadState readState, string transientAccessToken) =>
        ObserveCoreAsync(ticket, request, httpStatus, boundedBody, readState, transientAccessToken, false, true);

    private async Task<GoogleLongAudioObservation> ObserveCoreAsync(Guid ticket, GoogleLongAudioRequest request, int httpStatus,
        ReadOnlyMemory<byte> boundedBody, GoogleResponseReadState readState, string transientAccessToken, bool operationError, bool metadata)
    {
        lock (_gate)
        {
            if (_ticket != ticket || !Current(request)) return Missing(readState, boundedBody.Length, GoogleExplanationRetentionState.ExpiredOrChanged);
            _ticket = null; _request = null;
        }
        PrivateProviderErrorRecord record;
        try
        {
            var envelope = PrivateProviderEnvelopeRedactor.Create(httpStatus, boundedBody.Length, readState == GoogleResponseReadState.Complete,
                readState.ToString(), boundedBody, request.CopyPayload(), transientAccessToken, operationError, maximumInputBytes: GoogleLongAudioRequest.MaximumWireBytes, storageMetadata: metadata);
            record = new(Guid.NewGuid(), clock.GetUtcNow(), "Private Long Audio response evidence; inspect completeness and redaction flags.",
                envelope.Truncated, request.PayloadSha256, request.AttemptId.ToString("N"), envelope.DetailsOmitted, envelope);
            if (JsonSerializer.SerializeToUtf8Bytes(record).Length > PrivateRecordProtection.MaximumPlaintextBytes)
                return Missing(readState, boundedBody.Length, GoogleExplanationRetentionState.IneligibleOrUnparseable);
        }
        catch (Exception error) when (NonFatal(error)) { return Missing(readState, boundedBody.Length, GoogleExplanationRetentionState.IneligibleOrUnparseable); }
        if (Interlocked.CompareExchange(ref _writerBusy, 1, 0) != 0) return Missing(readState, boundedBody.Length, GoogleExplanationRetentionState.StorageBusy);
        Task<bool> write;
        try
        {
            using (ExecutionContext.SuppressFlow()) write = Task.Run(() => SaveAndReadBack(record)); // Only an already-redacted bounded record enters the worker.
        }
        catch (Exception error) when (NonFatal(error)) { Volatile.Write(ref _writerBusy, 0); return Missing(readState, boundedBody.Length, GoogleExplanationRetentionState.StorageFailed); }
        bool saved;
        try { saved = await write.ConfigureAwait(false); }
        catch (Exception error) when (NonFatal(error)) { saved = false; }
        var response = record.Response!;
        return new(saved ? record.Id : null, readState, Parse(response.ParseState), response.ErrorStatus, boundedBody.Length,
            saved ? GoogleExplanationRetentionState.SavedPrivately : GoogleExplanationRetentionState.StorageFailed,
            saved, response.Truncated, response.DetailsOmitted);
    }

    private bool SaveAndReadBack(PrivateProviderErrorRecord record)
    {
        try
        {
            if (!persistence.Save(record)) return false;
            var result = persistence.ReadWithStatus();
            if (result.Status != PrivateProviderErrorReadStatus.Success) return false;
            var found = result.Records.SingleOrDefault(x => x.Id == record.Id);
            return found is not null && SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(found)).AsSpan()
                .SequenceEqual(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record)));
        }
        catch (Exception error) when (NonFatal(error)) { return false; }
        finally { Volatile.Write(ref _writerBusy, 0); }
    }

    private static GoogleLongAudioObservation Missing(GoogleResponseReadState state, int bytes, GoogleExplanationRetentionState retention) =>
        new(null, state, GoogleLongAudioParseState.Unavailable, GoogleErrorStatus.NotProvided, bytes, retention, false, true, true);
    private static GoogleLongAudioParseState Parse(string state) => state switch
    {
        "Json" => GoogleLongAudioParseState.Json,
        "Empty" => GoogleLongAudioParseState.Empty,
        "NonJsonOrMalformed" => GoogleLongAudioParseState.NonJsonOrMalformed,
        "UnstructuredContentExcluded" => GoogleLongAudioParseState.Excluded,
        "InvalidUtf8" => GoogleLongAudioParseState.InvalidUtf8,
        "RedactionUnavailable" => GoogleLongAudioParseState.RedactionUnavailable,
        "SuccessBodyExcluded" => GoogleLongAudioParseState.SuccessBodyExcluded,
        _ => GoogleLongAudioParseState.Unavailable,
    };
    private static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}
