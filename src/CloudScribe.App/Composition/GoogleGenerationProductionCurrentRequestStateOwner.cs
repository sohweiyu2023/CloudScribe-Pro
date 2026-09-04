namespace CloudScribe.App.Composition;

/// <summary>
/// Owns one coherent, request-bound Stage6 pre-compile evidence snapshot.
/// The complete tuple is replaced atomically so production code never assembles
/// account/pricing/trust/queue/reconciliation evidence from independent "latest" reads.
/// </summary>
public sealed class GoogleGenerationProductionCurrentRequestStateOwner
{
    private readonly System.Threading.Lock _gate = new();
    private GoogleGenerationProductionCompileEvidence? _current;
    private long _version;

    public CurrentRequest Publish(GoogleGenerationProductionCompileEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Validate(evidence);

        lock (_gate)
        {
            _current = evidence;
            _version = checked(_version + 1);
            return new CurrentRequest(_version, evidence);
        }
    }

    public CurrentRequest ResolveCurrent()
    {
        lock (_gate)
        {
            GoogleGenerationProductionCompileEvidence evidence = _current
                ?? throw new InvalidOperationException(
                    "No coherent current Google generation request is available for production compilation.");
            return new CurrentRequest(_version, evidence);
        }
    }

    public CurrentRequest ClaimCurrent()
    {
        lock (_gate)
        {
            GoogleGenerationProductionCompileEvidence evidence = _current
                ?? throw new InvalidOperationException(
                    "No coherent current Google generation request is available for production compilation.");
            CurrentRequest claimed = new(_version, evidence);
            _current = null;
            return claimed;
        }
    }

    public void RestoreIfUnchanged(CurrentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_current is null && _version == request.Version)
            {
                _current = request.Evidence;
            }
        }
    }

    private static void Validate(GoogleGenerationProductionCompileEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.PricingProvenanceId)
            || string.IsNullOrWhiteSpace(evidence.ProjectId)
            || string.IsNullOrWhiteSpace(evidence.ModelId)
            || string.IsNullOrWhiteSpace(evidence.IdempotencyKey)
            || string.IsNullOrWhiteSpace(evidence.Currency))
        {
            throw new InvalidOperationException(
                "Google generation current request evidence is incomplete.");
        }

        if (evidence.RequestRevision < 0
            || evidence.Scale < 0
            || evidence.CurrentEstimateMinorUnits < 0)
        {
            throw new InvalidOperationException(
                "Google generation current request revision or pricing evidence is invalid.");
        }
    }

    public sealed record CurrentRequest(
        long Version,
        GoogleGenerationProductionCompileEvidence Evidence);
}
