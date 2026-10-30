using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit one-review local reconnection; no generation or policy admission API.</summary>
public sealed partial class GoogleCredentialRepairService : IDisposable
{
    private readonly GoogleCredentialRepairJournalStore _journal;
    private readonly ICredentialVault _vault;
    private readonly ICredentialPresenceReader _presence;
    private readonly GoogleCredentialRepairVerifier _verifier;
    private readonly AppPaths _paths;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ITimer _cleanup;
    private readonly System.Threading.Lock _lifetimeGate = new();
    private bool _disposed;
    private int _activeConfirms;
    private readonly CancellationTokenSource _shutdown = new();

    public GoogleCredentialRepairService(GoogleCredentialRepairJournalStore journal, ICredentialVault vault,
        ICredentialPresenceReader presence, GoogleCredentialRepairVerifier verifier, AppPaths paths, TimeProvider clock)
    {
        _journal = journal; _vault = vault; _presence = presence; _verifier = verifier; _paths = paths; _clock = clock;
        _cleanup = clock.CreateTimer(_ => Expire(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<GoogleCredentialRepairReview> PrepareAsync(string accountId, string expectedEmail,
        ReadOnlyMemory<char> candidateJson, CancellationToken token = default)
    {
        using var operation = LinkedOperation(token);
        token = operation.Token;
        Expire();
        if (_pending.Count >= 4) throw new InvalidOperationException("Close earlier credential reviews first.");
        var state = await _journal.ReadAsync(accountId, token).ConfigureAwait(false);
        string oldReference = state.Account.Reference.CredentialReference!.TargetName;
        await RequireAbsentAsync(oldReference, token).ConfigureAwait(false);
        Guid repairId = state.Active?.RepairId ?? Guid.NewGuid();
        string reference = state.Active?.StagedReference ?? $"google-repair.{repairId:N}";
        var candidate = await _verifier.VerifyAsync(candidateJson, state.Account, state.ProjectId, expectedEmail, reference, token).ConfigureAwait(false);
        try
        {
            if (state.Active is { } active && active.Identity != candidate.Identity)
                throw new InvalidOperationException("This account has an interrupted repair with a different candidate; no overwrite is permitted.");
            DateTimeOffset expires = _clock.GetUtcNow().AddMinutes(5);
            string profile = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_paths.RootDirectory)));
            string build = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(GoogleCredentialRepairService).Assembly.Location)));
            Guid reviewId = Guid.NewGuid();
            string hash = RecoveryFinancialSnapshot.Hash(new
            {
                Kind = "explicit-existing-google-reconnection-v1",
                reviewId,
                repairId,
                state.Account,
                state.ProjectId,
                state.FinancialSha256,
                candidate.Identity,
                reference,
                expires,
                profile,
                build
            });
            var review = new GoogleCredentialRepairReview(reviewId, repairId, hash, accountId, candidate.Identity,
                oldReference, reference, expires, state.Active is not null, profile, build);
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pending.Count >= 4 || !_pending.TryAdd(reviewId, new(state, review, candidate)))
                    throw new InvalidOperationException("Close earlier reviews first.");
            }
            return review;
        }
        catch { candidate.Dispose(); throw; }
    }

    public void Discard(Guid reviewId)
    {
        if (_pending.TryRemove(reviewId, out var value)) value.Candidate.Dispose();
    }

    public Task<string> ReadStatusAsync(string accountId, CancellationToken token = default) => _journal.StatusAsync(accountId, token);

    private void Expire()
    {
        foreach (var item in _pending)
            if (_clock.GetUtcNow() >= item.Value.Review.ExpiresAtUtc) Discard(item.Key);
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _cleanup.Dispose();
            foreach (var key in _pending.Keys) Discard(key);
            if (_activeConfirms == 0) _gate.Dispose();
        }
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private CancellationTokenSource LinkedOperation(CancellationToken token)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        }
    }

    private sealed record Pending(GoogleCredentialRepairState State, GoogleCredentialRepairReview Review, GoogleVerifiedRepairCandidate Candidate);
}
