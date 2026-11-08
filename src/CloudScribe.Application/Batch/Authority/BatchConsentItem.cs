namespace CloudScribe.Application.Batch.Authority;

/// <summary>One approved file: its exact source bytes, the engine chosen for it, its exact plan (the ordinary engine's plan
/// hash, or the Long Audio request binding), and the most it may reserve. <see cref="LongAudio"/> is set only for Long Audio.</summary>
public sealed record BatchConsentItem(Guid ItemId, string SourceSha256, Guid DocumentId, Guid RevisionId, string PlanSha256, long MaximumMinorUnits,
    string Engine = BatchEngines.CompleteArticle, BatchLongAudioApproval? LongAudio = null, string? NarrationTextSha256 = null, BatchNarrationSource? NarrationSource = null);
