namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only saved coverage and planning price. This is never a spend review, reservation or dispatch authority.</summary>
public sealed record ChirpArticlePlanningInspection(Guid DocumentId, string PlanSha256, string VoiceName, string LanguageCode,
    int SourceScalars, int SavedParts, int TotalParts, int RemainingRequests, int RemainingScalars,
    decimal PlanningUsdPerMillionCharacters, decimal PlanningListPriceUsd, long PlanningRoundedCentHolds,
    IReadOnlyList<int> RecoveryRequiredOrdinals);
