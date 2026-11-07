namespace CloudScribe.Infrastructure.Generation;

/// <summary>Non-mutating local admission preflight. Durable authorization remains independently mandatory.</summary>
public interface IGoogleLongAudioSubmissionPreflight
{
    ValueTask RequireBeforeJournalAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken);
}
