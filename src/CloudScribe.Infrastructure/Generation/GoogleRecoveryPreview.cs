using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleRecoveryPreview(Guid PreviewId, string ReviewSha256, string LogicalItemId,
    GoogleRecoveryIntent Intent, GoogleGenerationPersistedQueueState PreviousState,
    GoogleRecoveryBudgetReview Budget, long AdditionalMaximumMinorUnits, string FinancialStateSha256,
    GoogleRecoveryAssociationProof AssociationProof);
