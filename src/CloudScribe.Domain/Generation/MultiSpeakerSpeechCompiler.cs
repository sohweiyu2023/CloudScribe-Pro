namespace CloudScribe.Domain.Generation;

public sealed record MultiSpeakerCompiledTurn(
    int StartNodeIndex,
    int EndNodeIndex,
    string SpeakerRole,
    SpeakerVoiceBinding Voice,
    IReadOnlyList<SpeechPlanNode> Nodes);

public static class MultiSpeakerSpeechCompiler
{
    public static IReadOnlyList<MultiSpeakerCompiledTurn> Compile(SpeechPlan plan, MultiSpeakerVoiceMap voiceMap)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(voiceMap);
        voiceMap = voiceMap.Validate();

        var turns = new List<MultiSpeakerCompiledTurn>();
        string? activeSpeaker = null;
        var start = -1;
        var nodes = new List<SpeechPlanNode>();

        void Flush(int endIndex)
        {
            if (nodes.Count == 0) return;
            if (activeSpeaker is null)
                throw new InvalidOperationException("Multi-speaker plans must identify a speaker before speakable content.");
            turns.Add(new MultiSpeakerCompiledTurn(start, endIndex, activeSpeaker, voiceMap.Resolve(activeSpeaker), nodes.ToArray()));
            nodes.Clear();
            start = -1;
        }

        for (var index = 0; index < plan.Nodes.Count; index++)
        {
            var node = plan.Nodes[index];
            if (node is SpeechSpeakerChange speakerChange)
            {
                Flush(index - 1);
                activeSpeaker = speakerChange.SpeakerId;
                _ = voiceMap.Resolve(activeSpeaker);
                continue;
            }

            if (activeSpeaker is null)
                throw new InvalidOperationException($"Node {index} appears before the first speaker change.");

            // Voice selection is controlled exclusively by the immutable speaker map.
            if (node is SpeechVoice)
                throw new InvalidOperationException("Inline voice changes are forbidden in a pinned multi-speaker plan.");

            if (start < 0) start = index;
            nodes.Add(node);
        }

        Flush(plan.Nodes.Count - 1);
        if (turns.Count == 0)
            throw new InvalidOperationException("Multi-speaker plan produced no speakable turns.");
        return turns;
    }
}
