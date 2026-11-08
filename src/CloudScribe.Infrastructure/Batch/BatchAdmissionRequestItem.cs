using CloudScribe.Application.Batch.Authority;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>One file in a batch request. <see cref="Engine"/> is null when no allowed engine can take the file
/// (<see cref="Note"/> says why). <see cref="ExactMaximum"/> is true when the maximum comes from the engine's own exact
/// review rather than the pre-flight estimate. <see cref="NeedsArticleAdmission"/> marks ordinary-engine files that still
/// need an installed article admission; they stop with nothing sent until they have one.</summary>
public sealed record BatchAdmissionRequestItem(Guid ItemId, int Order, string SourceSha256, int Characters, Guid DocumentId, Guid RevisionId,
    string? Engine, string PlanSha256, int Parts, long EstimatedMaximumMinorUnits, bool ExactMaximum, bool NeedsArticleAdmission, string? Note,
    ChirpArticlePlanProposal? Plan, BatchLongAudioApproval? LongAudio, string? NarrationTextSha256 = null, CloudScribe.Application.Batch.BatchNarrationSource? NarrationSource = null);
