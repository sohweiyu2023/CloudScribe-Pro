using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Atomically owns the request-bound authorization facts that do not have an independent
/// production store. The snapshot is internal to production composition: shell/request callers
/// cannot publish a complete <see cref="GoogleGenerationProductionCompileEvidence"/> object.
/// </summary>
internal sealed class GoogleGenerationProductionAuthorizationSnapshotStateOwner
{
    private readonly System.Threading.Lock _gate = new();
    private AuthorizationSnapshot? _current;
    private long _version;

    public CurrentSnapshot Publish(AuthorizationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();

        lock (_gate)
        {
            _current = snapshot;
            _version = checked(_version + 1);
            return new CurrentSnapshot(_version, snapshot);
        }
    }

    public CurrentSnapshot ClaimCurrent()
    {
        lock (_gate)
        {
            AuthorizationSnapshot snapshot = _current
                ?? throw new InvalidOperationException(
                    "No request-bound Google generation production authorization snapshot is available.");
            CurrentSnapshot claimed = new(_version, snapshot);
            _current = null;
            return claimed;
        }
    }

    public void RestoreIfUnchanged(CurrentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (_current is null && _version == snapshot.Version)
            {
                _current = snapshot.Snapshot;
            }
        }
    }

    internal sealed record AuthorizationSnapshot
    {
        public required string AccountId { get; init; }

        public required string ProjectId { get; init; }

        public required string ModelId { get; init; }

        public required string IdempotencyKey { get; init; }

        public required GoogleGenerationAccount Account { get; init; }

        public required GoogleCapabilitySnapshot Capabilities { get; init; }

        public required string PricingProvenanceId { get; init; }

        public required int RequestRevision { get; init; }

        public required GenerationCacheTrustContext AdmittedTrust { get; init; }

        public required GoogleGenerationPersistedQueueState PreviousState { get; init; }

        public required GoogleGenerationPersistedQueueState CurrentState { get; init; }

        public required GoogleGenerationReconciliationResolutionEvidence ResolutionEvidence { get; init; }

        public required bool AccountAuthorized { get; init; }

        public required bool ProjectAuthorized { get; init; }

        public required bool CapabilityCurrent { get; init; }

        public required bool PricingCurrent { get; init; }

        public required bool AdmissionCurrent { get; init; }

        public required bool AccountCredentialAvailable { get; init; }

        public required bool PricingApproved { get; init; }

        public required bool PostCompileLimitsSatisfied { get; init; }

        public required string Currency { get; init; }

        public required int Scale { get; init; }

        public required long CurrentEstimateMinorUnits { get; init; }

        public required DateTimeOffset CapturedAtUtc { get; init; }

        public void Validate()
        {
            ValidateRequiredText(AccountId, nameof(AccountId));
            ValidateRequiredText(ProjectId, nameof(ProjectId));
            ValidateRequiredText(ModelId, nameof(ModelId));
            ValidateRequiredText(IdempotencyKey, nameof(IdempotencyKey));
            ValidateRequiredText(PricingProvenanceId, nameof(PricingProvenanceId));
            ValidateRequiredText(Currency, nameof(Currency));

            if (Account is null
                || Capabilities is null
                || AdmittedTrust is null
                || PreviousState is null
                || CurrentState is null
                || ResolutionEvidence is null)
            {
                throw new InvalidOperationException(
                    "Request-bound Google authorization snapshot is missing required production evidence.");
            }

            if (!AccountAuthorized
                || !ProjectAuthorized
                || !CapabilityCurrent
                || !PricingCurrent
                || !AdmissionCurrent
                || !AccountCredentialAvailable
                || !PricingApproved
                || !PostCompileLimitsSatisfied)
            {
                throw new InvalidOperationException(
                    "Request-bound Google authorization snapshot contains rejected, stale, or unavailable production authorization evidence.");
            }

            if (RequestRevision < 0
                || Scale is < 0 or > 9
                || CurrentEstimateMinorUnits < 0
                || CapturedAtUtc == default)
            {
                throw new InvalidOperationException(
                    "Request-bound Google authorization snapshot contains invalid revision, pricing, or capture-time values.");
            }
        }

        private static void ValidateRequiredText(string value, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Request-bound Google authorization snapshot is missing required {propertyName} evidence.");
            }
        }
    }

    internal sealed record CurrentSnapshot(long Version, AuthorizationSnapshot Snapshot);
}
