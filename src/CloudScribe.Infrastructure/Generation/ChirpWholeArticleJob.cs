namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpWholeArticleJob(Guid JobId, Guid DocumentId, string PlanSha256,
    string PolicySha256, string ReviewSha256, long MaximumAdditionalMinorUnits,
    DateTimeOffset ExpiresAtUtc, DateTimeOffset LastObservedAtUtc, ChirpWholeArticleState State, int? ActiveOrdinal, string ReasonCode,
    ChirpWholeArticleOutput? Output = null, int CatalogRefreshes = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int MaximumRecoveryRequests = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    long MaximumRecoveryMinorUnits = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool CompletionBound = false);
