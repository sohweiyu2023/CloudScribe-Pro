using System.Security.Cryptography;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Owns the single exact pre-authorization Google submission produced by the current compile path.
/// Approval callers consume this owned state instead of reconstructing envelope, snapshot, or estimate
/// independently. The state is removed only after the supplied action succeeds.
/// </summary>
public sealed class GoogleGenerationProductionPendingApprovalStateOwner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingState? _current;

    public void Publish(PendingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        Volatile.Write(ref _current, state);
    }

    public async Task ExecuteCurrentAsync(
        Func<PendingState, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingState current = Volatile.Read(ref _current)
                ?? throw new InvalidOperationException(
                    "Google generation has no current compiled submission awaiting explicit spend approval.");
            current.Validate();
            await action(current, cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(Volatile.Read(ref _current), current))
            {
                Volatile.Write(ref _current, null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PendingState?> ResolveCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _current));
    }

    public void Invalidate()
    {
        Volatile.Write(ref _current, null);
    }

    public sealed record PendingState(
        GoogleGenerationSubmissionEnvelope Envelope,
        GoogleGenerationUiExecutionSnapshot Snapshot,
        string Currency,
        int Scale,
        long CurrentEstimateMinorUnits)
    {
        public PendingState Validate()
        {
            if (Envelope is null)
            {
                throw new InvalidOperationException("Google generation pending approval requires a submission envelope.");
            }

            if (Snapshot is null)
            {
                throw new InvalidOperationException("Google generation pending approval requires a UI execution snapshot.");
            }

            GoogleGenerationProductionUiSnapshotValidator.Validate(Snapshot);
            if (string.IsNullOrWhiteSpace(Currency))
            {
                throw new InvalidOperationException("Google generation pending approval currency is required.");
            }

            if (Scale is < 0 or > 9)
            {
                throw new InvalidOperationException("Google generation pending approval scale must be between 0 and 9.");
            }

            if (CurrentEstimateMinorUnits < 0)
            {
                throw new InvalidOperationException("Google generation pending approval estimate cannot be negative.");
            }

            RequireEqual(Envelope.AccountId, Snapshot.ProviderRequest.AccountId,
                "Pending Google generation account differs from the exact compiled provider request.");
            RequireEqual(Envelope.CapabilityProvenanceId, Snapshot.AdmittedTrust.CapabilityIdentity,
                "Pending Google generation capability provenance differs from admitted trust.");
            RequireEqual(Envelope.PricingProvenanceId, Snapshot.AdmittedTrust.PricingIdentity,
                "Pending Google generation pricing provenance differs from admitted trust.");
            RequireEqual(Envelope.VoiceName, Snapshot.UiSelection.VoiceId,
                "Pending Google generation voice differs from the UI selection.");
            RequireEqual(Envelope.AudioEncoding, Snapshot.ProviderRequest.OutputFormat,
                "Pending Google generation encoding differs from the exact compiled provider request.");

            ReadOnlySpan<byte> compiledPayload = Snapshot.ProviderRequest.CompiledPayload.Span;
            if (Envelope.CompiledPayloadBytes != compiledPayload.Length)
            {
                throw new InvalidOperationException(
                    "Pending Google generation compiled payload length differs from the submission envelope.");
            }

            string compiledPayloadSha256 = Convert.ToHexString(SHA256.HashData(compiledPayload)).ToLowerInvariant();
            RequireEqual(Envelope.CompiledPayloadSha256, compiledPayloadSha256,
                "Pending Google generation compiled payload digest differs from the submission envelope.");
            return this;
        }

        private static void RequireEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
