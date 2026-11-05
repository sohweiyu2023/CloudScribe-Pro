namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpWholeArticleReview(Guid PreviewId, string ReviewSha256, Guid DocumentId,
    string PlanSha256, string VoiceName, string Currency, int Scale, int AcceptedParts, int TotalParts,
    int RemainingRequests, long MaximumAdditionalMinorUnits, long RetainedCommitmentsMinorUnits,
    DateTimeOffset ExpiresAtUtc, int MaximumCatalogRefreshes, DateTimeOffset ConfirmByUtc,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int MaximumRecoveryRequests = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    long MaximumRecoveryMinorUnits = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool CompletionBound = false);
