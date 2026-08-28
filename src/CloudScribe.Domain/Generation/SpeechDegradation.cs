namespace CloudScribe.Domain.Generation;

public sealed record SpeechDegradation(
    int NodeIndex,
    SpeechDegradationKind Kind,
    string Reason,
    string? ProviderReplacement)
{
    public int NodeIndex { get; init; } = NodeIndex >= 0
        ? NodeIndex
        : throw new ArgumentOutOfRangeException(nameof(NodeIndex));

    public SpeechDegradationKind Kind { get; init; } = Enum.IsDefined(Kind)
        ? Kind
        : throw new ArgumentOutOfRangeException(nameof(Kind));

    public string Reason { get; init; } = Require(Reason);

    public string? ProviderReplacement { get; init; } = string.IsNullOrWhiteSpace(ProviderReplacement)
        ? null
        : ProviderReplacement.Trim();

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
