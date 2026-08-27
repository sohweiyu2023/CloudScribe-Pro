using System.Collections.ObjectModel;

namespace CloudScribe.Domain.Generation;

public sealed class SpeechPlan
{
    public SpeechPlan(string languageTag, IEnumerable<SpeechPlanNode> nodes, string provenanceId)
    {
        LanguageTag = Require(languageTag, nameof(languageTag));
        ProvenanceId = Require(provenanceId, nameof(provenanceId));
        ArgumentNullException.ThrowIfNull(nodes);

        var materialized = nodes.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("A speech plan requires at least one node.", nameof(nodes));
        }

        if (materialized.Any(static node => node is null))
        {
            throw new ArgumentException("Speech plan nodes cannot contain null entries.", nameof(nodes));
        }

        Nodes = new ReadOnlyCollection<SpeechPlanNode>(materialized);
    }

    public string LanguageTag { get; }

    public IReadOnlyList<SpeechPlanNode> Nodes { get; }

    public string ProvenanceId { get; }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
