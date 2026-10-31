namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact offline compilation proposal. This is never spend, lineage or dispatch authority.</summary>
public sealed record ChirpArticlePlanProposal(
    Guid DocumentId, Guid RevisionId, string SourceSha256, int SourceScalars, string LanguageCode, string VoiceName,
    string PlannerVersion, string PlanSha256, IReadOnlyList<ChirpArticlePlanPart> Parts,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ChirpFailedPlanRefinement? Refinement = null);
