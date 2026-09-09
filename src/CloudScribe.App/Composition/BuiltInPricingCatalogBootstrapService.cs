using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

/// <summary>
/// Exposes the already authenticated v2.22 pricing seed to the normal desktop flow without
/// silently trusting or activating it. The embedded carrier and exact seed bytes are authenticated
/// by <see cref="V222ControlSet"/>; the catalog still enters history as ValidUnsigned and therefore
/// requires an explicit user-confirmed ManualUnsigned activation.
/// </summary>
public sealed class BuiltInPricingCatalogBootstrapService(
    V222ControlSet controls,
    IPricingCatalogAdmissionService admission,
    IPricingCatalogHistoryStore history)
{
    private readonly V222ControlSet _controls = controls ?? throw new ArgumentNullException(nameof(controls));
    private readonly IPricingCatalogAdmissionService _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    private readonly IPricingCatalogHistoryStore _history = history ?? throw new ArgumentNullException(nameof(history));

    public async Task<PricingCatalogSnapshot> ActivateAsync(
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmed)
            throw new InvalidOperationException("Built-in pricing activation requires explicit user confirmation.");

        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> bytes = _controls.PricingSeedUtf8;
        PricingCatalogDryRunResult dryRun = _admission.DryRun(bytes);
        if (dryRun.TrustState != PricingCatalogTrustState.ValidUnsigned)
        {
            throw new InvalidOperationException(
                $"Authenticated built-in pricing seed is not admissible as the expected unsigned catalog: {dryRun.TrustState} · {dryRun.Summary}");
        }

        PricingCatalogSnapshot snapshot = await _history.SaveSnapshotAsync(
            bytes,
            PricingCatalogTrustState.ValidUnsigned,
            new PricingCatalogSource(
                PricingCatalogSourceKind.BuiltInSeed,
                $"Authenticated built-in v2.22 pricing · {V222ControlSet.CatalogVersion}"),
            signatureKeyId: null,
            cancellationToken).ConfigureAwait(false);

        PricingCatalogSnapshot? active = await _history.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null && string.Equals(active.Sha256, snapshot.Sha256, StringComparison.Ordinal))
            return active;

        IReadOnlyList<PricingCatalogActivation> activations = await _history
            .ListActivationsAsync(cancellationToken)
            .ConfigureAwait(false);
        long expectedSequence = activations.Count == 0 ? 0 : activations[0].Sequence;

        await _history.ActivateAsync(
            new PricingCatalogActivationRequest(
                snapshot.Id,
                snapshot.Sha256,
                expectedSequence,
                PricingCatalogActivationKind.Activate,
                PricingCatalogApprovalKind.ManualUnsigned,
                userConfirmed: true,
                reason: "User explicitly activated the authenticated built-in pricing catalog for billable generation."),
            cancellationToken).ConfigureAwait(false);

        return await _history.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Pricing activation committed without an active catalog snapshot.");
    }
}
