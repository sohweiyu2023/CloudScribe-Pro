using System.Collections.ObjectModel;

namespace CloudScribe.Domain.Generation;

public sealed class GenerationJobSnapshot
{
    public GenerationJobSnapshot(
        Guid jobId,
        Guid collectionId,
        int revision,
        DateTimeOffset capturedAtUtc,
        SpeechPlan speechPlan,
        string providerAccountId,
        string providerOperationId,
        string pricingProvenanceId,
        string runtimePolicyProvenanceId,
        IEnumerable<GenerationSegmentSnapshot> segments)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id cannot be empty.", nameof(jobId));
        }

        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("Collection id cannot be empty.", nameof(collectionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        SpeechPlan = speechPlan ?? throw new ArgumentNullException(nameof(speechPlan));
        ProviderAccountId = Require(providerAccountId, nameof(providerAccountId));
        ProviderOperationId = Require(providerOperationId, nameof(providerOperationId));
        PricingProvenanceId = Require(pricingProvenanceId, nameof(pricingProvenanceId));
        RuntimePolicyProvenanceId = Require(runtimePolicyProvenanceId, nameof(runtimePolicyProvenanceId));
        ArgumentNullException.ThrowIfNull(segments);

        var materialized = segments.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("A generation job requires at least one segment.", nameof(segments));
        }

        for (var index = 0; index < materialized.Length; index++)
        {
            if (materialized[index].Index != index)
            {
                throw new ArgumentException("Generation segment indexes must be contiguous and ordered from zero.", nameof(segments));
            }
        }

        JobId = jobId;
        CollectionId = collectionId;
        Revision = revision;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Segments = new ReadOnlyCollection<GenerationSegmentSnapshot>(materialized);
    }

    public Guid JobId { get; }

    public Guid CollectionId { get; }

    public int Revision { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public SpeechPlan SpeechPlan { get; }

    public string ProviderAccountId { get; }

    public string ProviderOperationId { get; }

    public string PricingProvenanceId { get; }

    public string RuntimePolicyProvenanceId { get; }

    public IReadOnlyList<GenerationSegmentSnapshot> Segments { get; }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
