using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleCredentialRepairService
{
    /// <summary>Called only by the trusted UI's separate exact-review local-write confirmation, never by file selection or verification.</summary>
    public async Task ConfirmAsync(Guid reviewId, string displayedReviewSha256, CancellationToken token = default)
    {
        using var operation = LinkedOperation(token);
        token = operation.Token;
        Pending pending;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pending.TryRemove(reviewId, out pending!))
                throw new InvalidOperationException("Review expired, was cancelled or was already consumed; inspect repair status before another action.");
            _activeConfirms++;
        }
        using var candidate = pending.Candidate;
        bool entered = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if (!string.Equals(displayedReviewSha256, pending.Review.ReviewSha256, StringComparison.Ordinal)) throw new InvalidOperationException("Displayed repair review changed.");
            Current(pending.Review, token);
            string leasePath = Path.Combine(_paths.RootDirectory, ".credential-repair.lock");
            GoogleCredentialRepairPaths.RequirePhysical(leasePath);
            using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await RequireAbsentAsync(pending.Review.OldCredentialReference, token).ConfigureAwait(false);
            bool created = await _journal.StartAsync(pending.State, pending.Review, token).ConfigureAwait(false);
            if (created || string.Equals(pending.State.Active?.Phase, "Prepared", StringComparison.Ordinal))
                await RequireAbsentAsync(pending.Review.NewCredentialReference, token).ConfigureAwait(false);
            await _journal.PhaseAsync(pending.State, pending.Review, "Staging", token).ConfigureAwait(false);
            await StageAsync(pending, token).ConfigureAwait(false);
            await VerifyStagedAsync(pending, token).ConfigureAwait(false);
            await _journal.PhaseAsync(pending.State, pending.Review, "Staged", token).ConfigureAwait(false);
            await RequireAbsentAsync(pending.Review.OldCredentialReference, token).ConfigureAwait(false);
            Current(pending.Review, token);
            await _journal.CommitAsync(pending.State, pending.Review, token).ConfigureAwait(false);
        }
        finally
        {
            if (entered) _gate.Release();
            lock (_lifetimeGate) { if (--_activeConfirms == 0 && _disposed) _gate.Dispose(); }
        }
    }

    private async Task StageAsync(Pending pending, CancellationToken token)
    {
        foreach (string name in Slots(pending.Review.NewCredentialReference))
        {
            Current(pending.Review, token);
            await _journal.PhaseAsync(pending.State, pending.Review, "Staging", token).ConfigureAwait(false);
            var reference = new CredentialReference(name);
            using var expected = await pending.Candidate.Material.ReadAsync(reference, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Verified candidate slot is unavailable.");
            var presence = await _presence.InspectAsync(reference, token).ConfigureAwait(false);
            if (presence == CredentialRolePresence.Unknown) throw new InvalidOperationException("Staging presence is unknown; reconcile before proceeding.");
            Current(pending.Review, token);
            if (presence == CredentialRolePresence.Absent)
                await _vault.StoreAsync(reference, expected.Value, token).ConfigureAwait(false);
            else
                await RequireEqualAsync(reference, expected, token).ConfigureAwait(false);
        }
    }

    private async Task VerifyStagedAsync(Pending pending, CancellationToken token)
    {
        foreach (string name in Slots(pending.Review.NewCredentialReference))
        {
            Current(pending.Review, token);
            var reference = new CredentialReference(name);
            using var expected = await pending.Candidate.Material.ReadAsync(reference, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Verified candidate slot is unavailable.");
            await RequireEqualAsync(reference, expected, token).ConfigureAwait(false);
        }
    }

    private async Task RequireEqualAsync(CredentialReference reference, CredentialSecret expected, CancellationToken token)
    {
        using var actual = await _vault.ReadAsync(reference, token).ConfigureAwait(false);
        if (actual is null || !CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(actual.Value.Span), MemoryMarshal.AsBytes(expected.Value.Span)))
            throw new InvalidOperationException("Staged material changed or is unavailable; it was not overwritten.");
    }

    private async Task RequireAbsentAsync(string reference, CancellationToken token)
    {
        foreach (string name in Slots(reference))
            if (await _presence.InspectAsync(new CredentialReference(name), token).ConfigureAwait(false) != CredentialRolePresence.Absent)
                throw new InvalidOperationException("Credential material is present or unknown; this absent-connection repair cannot overwrite it.");
    }

    private void Current(GoogleCredentialRepairReview review, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        token.ThrowIfCancellationRequested();
        if (_clock.GetUtcNow() >= review.ExpiresAtUtc) throw new InvalidOperationException("Repair approval expired; partial staging was retained for reconciliation.");
    }

    private static string[] Slots(string reference) =>
        [reference + GoogleServiceAccountCredentialOnboardingService.PrivateKeySuffix,
         reference + GoogleServiceAccountCredentialOnboardingService.MetadataSuffix, reference];
}
