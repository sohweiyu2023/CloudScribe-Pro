using System.Security.Cryptography;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Default-off observer. One exact UI-bound request object can consume one in-memory ticket.
/// No method dispatches, approves, retries, reads a vault or mutates queue/financial state.</summary>
public sealed partial class GooglePrivateErrorDiagnostics(IPrivateProviderErrorPersistence persistence, TimeProvider clock,
    CompletionWorkflowGate? completionGate = null) : IDisposable
{
    private readonly System.Threading.Lock _gate = new();
    public GooglePrivateErrorDiagnostics(string directory, TimeProvider clock, CompletionWorkflowGate? completionGate = null)
        : this(new FilePrivateProviderErrorStore(directory, clock), clock, completionGate) { }
    private ArmState? _arm;
    private Ticket? _ticket;
    private Guid? _active;
    private DateTimeOffset _activeExpires;
    private string? _activePayloadSha256;
    private long _activeStarted;
    private long _reviewEpoch;
    private bool _disposed;

    public long BeginOptInReview()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _arm = null; _ticket = null; return ++_reviewEpoch; }
    }

    public bool CompleteOptInReview(long review, PrivateErrorCaptureBinding binding, bool stillConfirmed)
    {
        lock (_gate)
        {
            if (review != _reviewEpoch || !stillConfirmed) return false;
            Arm(binding, explicitlyConfirmed: true); return true;
        }
    }

    public bool IsArmed { get { lock (_gate) return _arm is { } arm && !Expired(arm.ExpiresUtc, arm.Started); } }

    public void Arm(PrivateErrorCaptureBinding binding, bool explicitlyConfirmed)
    {
        completionGate?.RequireReady();
        ArgumentNullException.ThrowIfNull(binding);
        if (!explicitlyConfirmed || binding.DocumentId == Guid.Empty || binding.RevisionId == Guid.Empty
            || binding.RequestVersion < 0 || string.IsNullOrWhiteSpace(binding.AccountId)
            || string.IsNullOrWhiteSpace(binding.ProjectId) || string.IsNullOrWhiteSpace(binding.VoiceId)
            || string.IsNullOrWhiteSpace(binding.CredentialReferenceId) || binding.PayloadSha256.Length != 64
            || !binding.PayloadSha256.All(char.IsAsciiHexDigit)) throw new InvalidOperationException("An exact saved request and explicit diagnostic opt-in are required.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) throw new InvalidOperationException("A diagnostic response is still in flight.");
            _ticket = null;
            _arm = new(binding, clock.GetUtcNow().AddMinutes(10), clock.GetTimestamp());
        }
    }

    public void Disarm()
    {
        lock (_gate) { ++_reviewEpoch; _arm = null; _ticket = null; _active = null; }
    }

    public void Attach(GenerationProviderRequest request, PrivateErrorCaptureBinding current)
    {
        lock (_gate)
        {
            var arm = _arm; _arm = null; _ticket = null;
            if (arm is null || Expired(arm.ExpiresUtc, arm.Started) || arm.Binding != current
                || !string.Equals(request.AccountId, current.AccountId, StringComparison.Ordinal)
                || !string.Equals(Hash(request.CompiledPayload.Span), current.PayloadSha256, StringComparison.Ordinal)) return;
            _ticket = new(Guid.NewGuid(), request, arm.Binding, arm.ExpiresUtc, arm.Started);
        }
    }

    internal Guid? Begin(GenerationProviderRequest request, string credentialReference)
    {
        lock (_gate)
        {
            var ticket = _ticket; _ticket = null;
            if (ticket is null || !ReferenceEquals(ticket.Request, request) || Expired(ticket.ExpiresUtc, ticket.Started)
                || !string.Equals(credentialReference, ticket.Binding.CredentialReferenceId, StringComparison.Ordinal)
                || !string.Equals(Hash(request.CompiledPayload.Span), ticket.Binding.PayloadSha256, StringComparison.Ordinal)) return null;
            _active = ticket.Id;
            _activeExpires = ticket.ExpiresUtc;
            _activePayloadSha256 = ticket.Binding.PayloadSha256;
            _activeStarted = ticket.Started;
            return ticket.Id;
        }
    }

    internal bool IsReadyFor(GenerationProviderRequest request, string credentialReference)
    {
        lock (_gate)
        {
            var ticket = _ticket;
            return !_disposed && ticket is not null && ReferenceEquals(ticket.Request, request)
                && string.Equals(ticket.Binding.AccountId, request.AccountId, StringComparison.Ordinal)
                && string.Equals(ticket.Binding.CredentialReferenceId, credentialReference, StringComparison.Ordinal)
                && string.Equals(ticket.Binding.PayloadSha256, Hash(request.CompiledPayload.Span), StringComparison.Ordinal)
                && !Expired(ticket.ExpiresUtc, ticket.Started);
        }
    }

    internal bool IsActiveFor(Guid? ticket, ReadOnlyMemory<byte> payload)
    {
        lock (_gate)
            return !_disposed && ticket is not null && ticket == _active && !Expired(_activeExpires, _activeStarted)
                && string.Equals(_activePayloadSha256, Hash(payload.Span), StringComparison.Ordinal);
    }

    internal void Observe(Guid? ticket, int status, ReadOnlyMemory<byte> response, ReadOnlyMemory<byte> input, string token, string correlation)
    {
        lock (_gate)
        {
            if (ticket is null || ticket != _active) return;
            _active = null; // Consumed even for success, missing/malformed message or disk/redaction failure.
            try
            {
                if (Expired(_activeExpires, _activeStarted) || !string.Equals(_activePayloadSha256, Hash(input.Span), StringComparison.Ordinal)) { Volatile.Write(ref _writeState, 6); return; }
                if (status != 400 || PrivateProviderMessageRedactor.Extract(response, input, token) is not { } message) { Volatile.Write(ref _writeState, 5); return; }
                QueueWrite(message, Hash(input.Span), correlation);
            }
            catch (Exception error) when (NonFatal(error)) { Volatile.Write(ref _writeState, 4); }
        }
    }

    internal void End(Guid? ticket)
    {
        lock (_gate) { if (ticket == _active) _active = null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; ++_reviewEpoch; _arm = null; _ticket = null; _active = null;
        }
        // Best effort at orderly host disposal, never an unbounded shutdown or a durable-outcome dependency.
        try { _ = Volatile.Read(ref _pendingWrite).Wait(TimeSpan.FromMilliseconds(250)); }
        catch (AggregateException error) when (error.InnerExceptions.All(NonFatal)) { }
        GC.SuppressFinalize(this);
    }

    private static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private bool Expired(DateTimeOffset expiry, long started)
    {
        var elapsed = clock.GetElapsedTime(started, clock.GetTimestamp());
        return expiry <= clock.GetUtcNow() || elapsed < TimeSpan.Zero || elapsed >= TimeSpan.FromMinutes(10);
    }
    private sealed record ArmState(PrivateErrorCaptureBinding Binding, DateTimeOffset ExpiresUtc, long Started);
    private sealed record Ticket(Guid Id, GenerationProviderRequest Request, PrivateErrorCaptureBinding Binding, DateTimeOffset ExpiresUtc, long Started);
}
