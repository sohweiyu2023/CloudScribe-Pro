using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact native-owned child range; its permission and queue ownership are revalidated before dispatch.</summary>
public sealed record ChirpArticleRecoveryChild(Guid AuthorizationId, Guid JobId, Guid RootAuthorizationId,
    Guid ParentAuthorizationId, string PlanSha256, int CoverageOrdinal, int ChildSlot, int Depth,
    ChirpNarrationRange Range, string PayloadSha256, int PayloadBytes, string RecoveryGrantSha256);
