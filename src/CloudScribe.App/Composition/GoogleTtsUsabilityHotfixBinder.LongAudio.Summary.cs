using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static string LongAudioSummaryText(GoogleLongAudioRunSummary summary, DateTimeOffset now, TimeSpan? currentRun, long estimatedListPriceCents) =>
        LongAudioSummaryPresentation.Text(summary, now, currentRun, estimatedListPriceCents);
}
