namespace CloudScribe.Application.Batch;

/// <summary>Progress inside one file. Part counts are verified-audio coverage, not a time estimate.</summary>
public sealed record BatchItemProgress(Guid ItemId, string Stage, int SavedParts, int TotalParts);
