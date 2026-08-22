namespace CloudScribe.Domain.Generation;

public readonly record struct AuthorizedSpendCeiling(string CurrencyCode, long Units, int Scale)
{
    public AuthorizedSpendCeiling Validate()
    {
        if (string.IsNullOrWhiteSpace(CurrencyCode) || CurrencyCode.Length != 3 ||
            CurrencyCode.Any(static character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("Spend ceiling currency must be a three-letter uppercase code.", nameof(CurrencyCode));
        }

        if (Units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Units));
        }

        if (Scale is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(Scale));
        }

        return this;
    }

    public bool Allows(AuthorizedSpendCeiling actual)
    {
        Validate();
        actual.Validate();
        if (!string.Equals(CurrencyCode, actual.CurrencyCode, StringComparison.Ordinal) || Scale != actual.Scale)
        {
            return false;
        }

        return actual.Units <= Units;
    }
}

public sealed record GenerationSpendAuthorization(
    Guid CollectionId,
    AuthorizedSpendCeiling CollectionCeiling,
    IReadOnlyDictionary<Guid, AuthorizedSpendCeiling> ItemCeilings,
    string PricingProvenanceId,
    long ApprovedRevision)
{
    public void Validate()
    {
        if (CollectionId == Guid.Empty)
        {
            throw new ArgumentException("Collection id is required.", nameof(CollectionId));
        }

        CollectionCeiling.Validate();
        ArgumentNullException.ThrowIfNull(ItemCeilings);
        ArgumentException.ThrowIfNullOrWhiteSpace(PricingProvenanceId);
        if (ApprovedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ApprovedRevision));
        }

        foreach (var pair in ItemCeilings)
        {
            if (pair.Key == Guid.Empty)
            {
                throw new ArgumentException("Spend authorization item ids must be non-empty.", nameof(ItemCeilings));
            }

            pair.Value.Validate();
            if (!string.Equals(pair.Value.CurrencyCode, CollectionCeiling.CurrencyCode, StringComparison.Ordinal) ||
                pair.Value.Scale != CollectionCeiling.Scale)
            {
                throw new ArgumentException("Item and collection spend ceilings must use one exact currency and scale.", nameof(ItemCeilings));
            }
        }
    }

    public bool AllowsCollectionSpend(AuthorizedSpendCeiling actual, long currentRevision, string pricingProvenanceId)
    {
        Validate();
        return currentRevision == ApprovedRevision &&
            string.Equals(PricingProvenanceId, pricingProvenanceId, StringComparison.Ordinal) &&
            CollectionCeiling.Allows(actual);
    }
}

public sealed record GenerationCircuitBreakerKey(
    string ProviderStableId,
    string AccountId,
    string EndpointId,
    string RegionId,
    string OperationStableId)
{
    public GenerationCircuitBreakerKey Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(EndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(RegionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationStableId);
        return this;
    }
}

public sealed class GenerationCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;
    private int _consecutiveFailures;
    private long? _openedAtTimestamp;

    public GenerationCircuitBreaker(int failureThreshold, TimeSpan cooldown, TimeProvider timeProvider)
    {
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }

        if (cooldown <= TimeSpan.Zero || cooldown > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _failureThreshold = failureThreshold;
        _cooldown = cooldown;
    }

    public bool IsOpen
    {
        get
        {
            if (_openedAtTimestamp is not { } openedAt)
            {
                return false;
            }

            return _timeProvider.GetElapsedTime(openedAt, _timeProvider.GetTimestamp()) < _cooldown;
        }
    }

    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _openedAtTimestamp = null;
    }

    public void RecordFailure()
    {
        if (_consecutiveFailures < int.MaxValue)
        {
            _consecutiveFailures++;
        }

        if (_consecutiveFailures >= _failureThreshold && _openedAtTimestamp is null)
        {
            _openedAtTimestamp = _timeProvider.GetTimestamp();
        }
    }

    public bool MayAttempt()
    {
        if (_openedAtTimestamp is not { } openedAt)
        {
            return true;
        }

        if (_timeProvider.GetElapsedTime(openedAt, _timeProvider.GetTimestamp()) < _cooldown)
        {
            return false;
        }

        _openedAtTimestamp = null;
        _consecutiveFailures = 0;
        return true;
    }
}

public enum OutputQualityDisposition
{
    Accepted,
    Quarantined,
}

public sealed record OutputQualityAssessment(
    OutputQualityDisposition Disposition,
    IReadOnlyList<string> DiagnosticCodes)
{
    public static OutputQualityAssessment Evaluate(
        bool mediaValid,
        bool durationWithinTolerance,
        bool containsRequiredTimingMarks,
        IEnumerable<string>? diagnostics = null)
    {
        var codes = diagnostics?.Where(static code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (!mediaValid)
        {
            codes.Add("quality.media.invalid");
        }
        if (!durationWithinTolerance)
        {
            codes.Add("quality.duration.out-of-range");
        }
        if (!containsRequiredTimingMarks)
        {
            codes.Add("quality.timing-marks.missing");
        }

        return new OutputQualityAssessment(
            codes.Count == 0 ? OutputQualityDisposition.Accepted : OutputQualityDisposition.Quarantined,
            codes);
    }
}

public sealed record TimedTextCue(
    int Sequence,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string ProvenanceId)
{
    public TimedTextCue Validate()
    {
        if (Sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Sequence));
        }
        if (Start < TimeSpan.Zero || End <= Start)
        {
            throw new ArgumentOutOfRangeException(nameof(End));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProvenanceId);
        return this;
    }
}

public sealed class TimedTextTrack
{
    public TimedTextTrack(IEnumerable<TimedTextCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        Cues = cues.Select(static cue => cue.Validate()).OrderBy(static cue => cue.Sequence).ToArray();
        if (Cues.Select(static cue => cue.Sequence).Distinct().Count() != Cues.Count)
        {
            throw new ArgumentException("Timed-text cue sequence numbers must be unique.", nameof(cues));
        }
        for (var index = 1; index < Cues.Count; index++)
        {
            if (Cues[index].Start < Cues[index - 1].End)
            {
                throw new ArgumentException("Timed-text cues must not overlap.", nameof(cues));
            }
        }
    }

    public IReadOnlyList<TimedTextCue> Cues { get; }
}
