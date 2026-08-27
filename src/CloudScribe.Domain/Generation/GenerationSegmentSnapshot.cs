namespace CloudScribe.Domain.Generation;

public sealed record GenerationSegmentSnapshot(
    int Index,
    string StableContentHash,
    int TextElementCount,
    int CompiledPayloadCharacters,
    string CompiledPayload)
{
    public int Index { get; init; } = Index >= 0
        ? Index
        : throw new ArgumentOutOfRangeException(nameof(Index));

    public string StableContentHash { get; init; } = Require(StableContentHash, nameof(StableContentHash));

    public int TextElementCount { get; init; } = TextElementCount >= 0
        ? TextElementCount
        : throw new ArgumentOutOfRangeException(nameof(TextElementCount));

    public int CompiledPayloadCharacters { get; init; } = CompiledPayloadCharacters >= 0
        ? CompiledPayloadCharacters
        : throw new ArgumentOutOfRangeException(nameof(CompiledPayloadCharacters));

    public string CompiledPayload { get; init; } = CompiledPayload ?? throw new ArgumentNullException(nameof(CompiledPayload));

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
