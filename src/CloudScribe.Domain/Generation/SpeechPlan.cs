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

public abstract record SpeechPlanNode;

public sealed record SpeechText(string Text) : SpeechPlanNode
{
    public string Text { get; init; } = RequireText(Text);

    private static string RequireText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text;
    }
}

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

public sealed record SpeechProsody(decimal Rate, decimal PitchSemitones, decimal VolumeDb) : SpeechPlanNode
{
    public decimal Rate { get; init; } = Rate is > 0m and <= 4m
        ? Rate
        : throw new ArgumentOutOfRangeException(nameof(Rate), "Rate must be greater than zero and at most 4x.");

    public decimal PitchSemitones { get; init; } = PitchSemitones is >= -24m and <= 24m
        ? PitchSemitones
        : throw new ArgumentOutOfRangeException(nameof(PitchSemitones), "Pitch must be within +/-24 semitones.");

    public decimal VolumeDb { get; init; } = VolumeDb is >= -96m and <= 24m
        ? VolumeDb
        : throw new ArgumentOutOfRangeException(nameof(VolumeDb), "Volume must be between -96 dB and +24 dB.");
}

public sealed record SpeechPause(TimeSpan Duration) : SpeechPlanNode
{
    public TimeSpan Duration { get; init; } = Duration >= TimeSpan.Zero && Duration <= TimeSpan.FromMinutes(5)
        ? Duration
        : throw new ArgumentOutOfRangeException(nameof(Duration), "Pause duration must be between zero and five minutes.");
}

public sealed record SpeechEmphasis(SpeechEmphasisLevel Level) : SpeechPlanNode
{
    public SpeechEmphasisLevel Level { get; init; } = Enum.IsDefined(Level)
        ? Level
        : throw new ArgumentOutOfRangeException(nameof(Level));
}

public enum SpeechEmphasisLevel
{
    Reduced,
    Moderate,
    Strong,
}

public sealed record SpeechPronunciation(string Text, string Alphabet, string Phonemes) : SpeechPlanNode
{
    public string Text { get; init; } = Require(Text, nameof(Text));

    public string Alphabet { get; init; } = Require(Alphabet, nameof(Alphabet));

    public string Phonemes { get; init; } = Require(Phonemes, nameof(Phonemes));

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed record SpeechMark(string Name) : SpeechPlanNode
{
    public string Name { get; init; } = Require(Name);

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}

public sealed record SpeechChapter(string ChapterId, string Title) : SpeechPlanNode
{
    public string ChapterId { get; init; } = Require(ChapterId, nameof(ChapterId));

    public string Title { get; init; } = Require(Title, nameof(Title));

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed record SpeechSpeakerChange(string SpeakerId) : SpeechPlanNode
{
    public string SpeakerId { get; init; } = Require(SpeakerId);

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}

public sealed record SpeechTimestampRequest(string MarkName) : SpeechPlanNode
{
    public string MarkName { get; init; } = Require(MarkName);

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}

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

public enum SpeechDegradationKind
{
    Unsupported,
    Approximated,
    Omitted,
}

public sealed class CompiledSpeechPlan
{
    public CompiledSpeechPlan(
        SpeechPlan source,
        string providerId,
        string compiledPayload,
        IEnumerable<SpeechDegradation> degradations)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(compiledPayload);
        ArgumentNullException.ThrowIfNull(degradations);

        ProviderId = providerId.Trim();
        CompiledPayload = compiledPayload;
        Degradations = new ReadOnlyCollection<SpeechDegradation>(degradations.ToArray());
    }

    public SpeechPlan Source { get; }

    public string ProviderId { get; }

    public string CompiledPayload { get; }

    public IReadOnlyList<SpeechDegradation> Degradations { get; }
}
