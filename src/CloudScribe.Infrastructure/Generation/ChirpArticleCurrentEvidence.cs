using CloudScribe.Application.Documents;

namespace CloudScribe.Infrastructure.Generation;

internal sealed record ChirpArticleCurrentEvidence(DocumentSnapshot Document, AdmittedChirpArticle Article,
    string FinancialSha256, long ProjectCommittedMinorUnits, long AllocationRemainingMinorUnits);
