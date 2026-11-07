using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleLongAudioDocumentRun(LongAudioWorkflowStage Stage, GoogleLongAudioJobProgress Outcome,
    GoogleLongAudioArticleOutput? ArticleOutput = null)
{
    public bool SummaryUnavailable { get; init; }
}
