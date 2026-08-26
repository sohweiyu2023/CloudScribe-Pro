using System.Collections.ObjectModel;

namespace CloudScribe.Domain.Generation;

public sealed class SpeechPlanSegment
{
    public SpeechPlanSegment(int index, IEnumerable<SpeechPlanNode> nodes, int textElementCount)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ArgumentNullException.ThrowIfNull(nodes);
        if (textElementCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textElementCount));
        }

        var materialized = nodes.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("A speech segment cannot be empty.", nameof(nodes));
        }

        Index = index;
        Nodes = new ReadOnlyCollection<SpeechPlanNode>(materialized);
        TextElementCount = textElementCount;
    }

    public int Index { get; }

    public IReadOnlyList<SpeechPlanNode> Nodes { get; }

    public int TextElementCount { get; }
}
