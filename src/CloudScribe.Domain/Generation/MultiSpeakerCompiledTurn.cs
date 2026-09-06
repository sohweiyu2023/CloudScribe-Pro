namespace CloudScribe.Domain.Generation;

public sealed record MultiSpeakerCompiledTurn(
    int StartNodeIndex,
    int EndNodeIndex,
    string SpeakerRole,
    SpeakerVoiceBinding Voice,
    IReadOnlyList<SpeechPlanNode> Nodes);
