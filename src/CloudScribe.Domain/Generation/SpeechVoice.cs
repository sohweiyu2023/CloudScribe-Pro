namespace CloudScribe.Domain.Generation;

public sealed record SpeechVoice(string Role, string VoiceStableId) : SpeechPlanNode
{
    public string Role { get; init; } = Require(Role, nameof(Role));

    public string VoiceStableId { get; init; } = Require(VoiceStableId, nameof(VoiceStableId));

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
