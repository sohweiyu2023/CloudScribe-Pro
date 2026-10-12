using System.Security.Cryptography;
using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

/// <summary>
/// Exposes the already authenticated v2.22 pricing seed to the normal desktop flow without
/// silently trusting or activating it. Before activation the user supplies a current HTTPS
/// pricing source; CloudScribe retrieves that source without credentials and records the final
/// HTTPS URI, observation time, and SHA-256 as unsigned observational evidence. The user must
/// still explicitly confirm that the observed source matches the embedded catalog being activated.
/// </summary>
public sealed class BuiltInPricingCatalogBootstrapService(
    V222ControlSet controls,
    IPricingCatalogAdmissionService admission,
    IPricingCatalogHistoryStore history,
    HttpClient httpClient)
{
    private const int MaxPricingEvidenceBytes = 2 * 1024 * 1024;

    private readonly V222ControlSet _controls = controls ?? throw new ArgumentNullException(nameof(controls));
    private readonly IPricingCatalogAdmissionService _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    private readonly IPricingCatalogHistoryStore _history = history ?? throw new ArgumentNullException(nameof(history));
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<ActivationResult> VerifyCurrentSourceAndActivateAsync(
        Uri pricingSource,
        bool userConfirmedSourceMatchesCatalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pricingSource);
        if (!pricingSource.IsAbsoluteUri
            || !string.Equals(pricingSource.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Current pricing evidence source must be an absolute HTTPS URI.");
        }
        if (!userConfirmedSourceMatchesCatalog)
        {
            throw new InvalidOperationException(
                "Pricing activation requires explicit confirmation that the freshly observed source matches the catalog being activated.");
        }

        CurrentPricingObservation observation = await ObserveCurrentPricingAsync(pricingSource, cancellationToken)
            .ConfigureAwait(false);
        PricingCatalogSnapshot snapshot = await ActivateSeedAsync(observation, cancellationToken).ConfigureAwait(false);
        return new ActivationResult(snapshot, observation);
    }

    private async Task<CurrentPricingObservation> ObserveCurrentPricingAsync(
        Uri source,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Accept.ParseAdd("text/html,application/json,text/plain;q=0.9,*/*;q=0.1");
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        Uri finalUri = response.RequestMessage?.RequestUri ?? source;
        if (!string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Current pricing evidence redirected away from HTTPS.");
        if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > MaxPricingEvidenceBytes)
            throw new InvalidOperationException("Current pricing evidence exceeds the 2 MiB safety limit.");

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > MaxPricingEvidenceBytes)
                throw new InvalidOperationException("Current pricing evidence exceeds the 2 MiB safety limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total == 0)
            throw new InvalidOperationException("Current pricing evidence source returned an empty response.");

        string sha256 = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, total))).ToLowerInvariant();
        return new CurrentPricingObservation(finalUri, DateTimeOffset.UtcNow, sha256, total);
    }

    private async Task<PricingCatalogSnapshot> ActivateSeedAsync(
        CurrentPricingObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> bytes = _controls.PricingSeedUtf8;
        PricingCatalogDryRunResult dryRun = _admission.DryRun(bytes);
        if (dryRun.TrustState != PricingCatalogTrustState.ValidUnsigned)
        {
            throw new InvalidOperationException(
                $"Authenticated built-in pricing seed is not admissible as the expected unsigned catalog: {dryRun.TrustState} · {dryRun.StatusReason}");
        }

        string sourceDescription =
            $"Authenticated built-in v2.22 pricing · {V222ControlSet.CatalogVersion} · " +
            $"current-source={observation.SourceUri} · observed-utc={observation.ObservedAtUtc:O} · " +
            $"source-sha256={observation.Sha256} · source-bytes={observation.ByteCount} · " +
            "trust=unsigned-observation+explicit-user-match-confirmation";

        PricingCatalogSnapshot snapshot = await _history.SaveSnapshotAsync(
            bytes,
            PricingCatalogTrustState.ValidUnsigned,
            new PricingCatalogSource(PricingCatalogSourceKind.BuiltInSeed, sourceDescription),
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
                reason: "User explicitly confirmed the freshly observed HTTPS pricing source matches the authenticated built-in catalog and activated it for billable generation."),
            cancellationToken).ConfigureAwait(false);

        return await _history.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Pricing activation committed without an active catalog snapshot.");
    }

    public sealed record CurrentPricingObservation(
        Uri SourceUri,
        DateTimeOffset ObservedAtUtc,
        string Sha256,
        int ByteCount);

    public sealed record ActivationResult(
        PricingCatalogSnapshot Snapshot,
        CurrentPricingObservation Observation);
}
