using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GenerationCollectionReleaseDecision(
    Guid CollectionId,
    int RequestRevision,
    string PricingProvenanceId,
    IReadOnlyList<GenerationProofResult> ProofResults,
    IReadOnlyList<OutputReservation> OutputReservations,
    DateTimeOffset EvaluatedAtUtc)
{
    public bool IsReleaseSafe => ProofResults.All(static result => result.IsReleaseSafe);
}

public sealed class GenerationCollectionReleaseCoordinator
{
    private readonly GenerationSpendGuard _spendGuard;
    private readonly GenerationProofPass _proofPass;
    private readonly GenerationOutputReservationService _outputReservationService;
    private readonly TimeProvider _timeProvider;

    public GenerationCollectionReleaseCoordinator(
        GenerationSpendGuard spendGuard,
        GenerationProofPass proofPass,
        GenerationOutputReservationService outputReservationService,
        TimeProvider timeProvider)
    {
        _spendGuard = spendGuard ?? throw new ArgumentNullException(nameof(spendGuard));
        _proofPass = proofPass ?? throw new ArgumentNullException(nameof(proofPass));
        _outputReservationService = outputReservationService ?? throw new ArgumentNullException(nameof(outputReservationService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public GenerationCollectionReleaseDecision Evaluate(
        GenerationCollectionEstimate estimate,
        GenerationApproval approval,
        GenerationSpendAuthorization spendAuthorization,
        IEnumerable<GenerationProofInput> proofInputs,
        AudioAssemblyPlan assemblyPlan,
        bool allowExplicitOutputReplacement = false)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(spendAuthorization);
        ArgumentNullException.ThrowIfNull(proofInputs);
        ArgumentNullException.ThrowIfNull(assemblyPlan);

        if (!approval.Authorizes(estimate))
        {
            throw new InvalidOperationException("The collection estimate is not covered by the exact current approval.");
        }

        if (spendAuthorization.CollectionId != estimate.CollectionId)
        {
            throw new InvalidOperationException("Spend authorization collection identity does not match the estimate.");
        }

        var collectionSpend = new AuthorizedSpendCeiling(estimate.Currency, estimate.ScaledTotal, estimate.Scale);
        _spendGuard.EnsureCollectionAuthorized(
            spendAuthorization,
            collectionSpend,
            estimate.RequestRevision,
            estimate.PricingProvenanceId);

        foreach (var item in estimate.Items)
        {
            var itemSpend = new AuthorizedSpendCeiling(item.Currency, item.ScaledAmount, item.Scale);
            _spendGuard.EnsureItemAuthorized(
                spendAuthorization,
                item.ItemId,
                itemSpend,
                estimate.RequestRevision,
                estimate.PricingProvenanceId);
        }

        var proofResults = _proofPass.EvaluateCollection(proofInputs);
        _proofPass.EnsureReleaseSafe(proofResults);

        var proofSegmentIds = proofResults.Select(static result => result.SegmentId).ToHashSet();
        var assemblySegmentIds = new HashSet<Guid>();
        foreach (var segment in assemblyPlan.Segments)
        {
            if (!Guid.TryParse(segment.SegmentId, out var segmentId))
            {
                throw new InvalidOperationException("Every assembly segment id must be the canonical GUID identity used by Proof Pass.");
            }

            if (!assemblySegmentIds.Add(segmentId))
            {
                throw new InvalidOperationException("Assembly plan contains a duplicate segment identity.");
            }
        }

        if (!proofSegmentIds.SetEquals(assemblySegmentIds))
        {
            throw new InvalidOperationException("Proof Pass and assembly plan must cover the exact same segment identities.");
        }

        var reservations = _outputReservationService.ReservePlanOutputs(assemblyPlan, allowExplicitOutputReplacement);
        return new GenerationCollectionReleaseDecision(
            estimate.CollectionId,
            estimate.RequestRevision,
            estimate.PricingProvenanceId,
            proofResults,
            reservations,
            _timeProvider.GetUtcNow());
    }
}
