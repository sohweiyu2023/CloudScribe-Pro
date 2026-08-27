using System.Collections.ObjectModel;

namespace CloudScribe.Domain.Generation;

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
