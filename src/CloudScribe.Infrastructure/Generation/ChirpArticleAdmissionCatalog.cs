namespace CloudScribe.Infrastructure.Generation;

/// <summary>The unchanged legacy authority and independently budgeted, exact fresh whole-article families.</summary>
public sealed record ChirpArticleAdmissionCatalog(ChirpArticleAdmissionPolicy? LegacyPolicy,
    IReadOnlyList<ChirpAdditionalAdmissionFamily> Families);
