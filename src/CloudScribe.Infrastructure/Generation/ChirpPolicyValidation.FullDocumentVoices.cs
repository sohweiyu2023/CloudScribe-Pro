namespace CloudScribe.Infrastructure.Generation;

public static partial class ChirpPolicyValidation
{
    private static void RequireFullDocumentVoiceFormat(ChirpArticleAdmissionPolicy policy)
    {
        foreach (var article in policy.Articles)
        {
            var plan = article.Proposal;
            bool neural2 = GoogleFullDocumentVoiceSupport.IsNeural2(plan.LanguageCode, plan.VoiceName);
            bool compiledBytes = string.Equals(plan.PlannerVersion, ChirpArticlePlanCompiler.Neural2Version, StringComparison.Ordinal);
            if (!neural2 && !compiledBytes) continue;
            if (!neural2 || !compiledBytes || plan.InputFormat is not null || plan.Refinement is not null
                || plan.SentenceSuccessor is not null || policy.SuccessorGrant is not null || policy.SentenceRecovery is not null)
                throw new InvalidDataException("Neural2 full-document admission requires its exact plain-text planner and cannot borrow Chirp sentence or diagnostic recovery.");
        }
    }
}
