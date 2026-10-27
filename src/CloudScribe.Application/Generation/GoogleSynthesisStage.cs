namespace CloudScribe.Application.Generation;

public enum GoogleSynthesisStage
{
    ResolvingCredential,
    Dispatching,
    ResponseHeaders,
    ResponseReadCompleted,
    Cancelled,
    Failed,
}
