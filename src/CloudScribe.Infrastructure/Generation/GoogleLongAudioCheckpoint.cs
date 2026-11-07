namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private journal data; never a media receipt or spend settlement.</summary>
public sealed record GoogleLongAudioCheckpoint(Guid AttemptId, string BindingSha256, string ProjectNumber,
    string Location, string CredentialReferenceId, string OutputGcsUri, string PayloadSha256, int PayloadBytes,
    GoogleLongAudioState State, string? OperationName, int HttpStatus, int? RpcCode, int Revision)
{
    public GoogleLongAudioObservation? Observation { get; init; }
    public bool OperationParseFailed { get; init; }
    public GoogleLongAudioFailureKind FailureKind { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PollWaitDeadlineElapsed { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVerifiedPollDeadline => PollWaitDeadlineElapsed && State == GoogleLongAudioState.Pollable
        && OperationName is not null && HttpStatus == 200 && !OperationParseFailed
        && Observation is
        {
            ReadbackVerified: true, Truncated: true,
            ReadState: CloudScribe.Domain.Generation.GoogleResponseReadState.Cancelled,
            Retention: CloudScribe.Domain.Generation.GoogleExplanationRetentionState.SavedPrivately
        };
    public override string ToString() => $"Long Audio {State}; private fields omitted; output unverified.";
}
