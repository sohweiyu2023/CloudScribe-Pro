using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed class GoogleGenerationProductionSpendApprovalService
{
    private readonly GoogleGenerationProductionPendingApprovalStateOwner _pendingStateOwner;
    private readonly GoogleGenerationProductionSubmissionStateOwner _stateOwner;

    public GoogleGenerationProductionSpendApprovalService(
        GoogleGenerationProductionPendingApprovalStateOwner pendingStateOwner,
        GoogleGenerationProductionSubmissionStateOwner stateOwner)
    {
        _pendingStateOwner = pendingStateOwner
            ?? throw new ArgumentNullException(nameof(pendingStateOwner));
        _stateOwner = stateOwner ?? throw new ArgumentNullException(nameof(stateOwner));
    }

    public async Task ApproveExplicitAsync(
        ApprovalConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        confirmation.Validate();
        if (!confirmation.ConfirmedByUser)
        {
            throw new InvalidOperationException(
                "Google generation spend requires an explicit user confirmation for the exact compiled submission.");
        }

        await _pendingStateOwner.ExecuteCurrentAsync(
            async (pending, currentCancellationToken) =>
            {
                GoogleGenerationSpendAuthorization authorization = GoogleGenerationSpendAuthorization.Create(
                    pending.Envelope,
                    pending.Currency,
                    pending.Scale,
                    pending.CurrentEstimateMinorUnits,
                    confirmation.AuthorizedMaximumMinorUnits);

                // Pending snapshots deliberately cannot claim spend approval. Promote only this
                // exact compiled snapshot after the user authorizes the envelope that hashes those
                // same provider bytes, then require the full runtime validator before persistence.
                var approvedSnapshot = pending.Snapshot with { PricingApproved = true };
                GoogleGenerationProductionUiSnapshotValidator.Validate(approvedSnapshot);

                _ = GoogleGenerationProductionRuntimeRequestFactory.Create(
                    authorization,
                    approvedSnapshot,
                    pending.CurrentEstimateMinorUnits).Validate();

                await _stateOwner.ApproveAsync(
                    authorization,
                    approvedSnapshot,
                    pending.CurrentEstimateMinorUnits,
                    currentCancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public sealed record ApprovalConfirmation(
        long AuthorizedMaximumMinorUnits,
        bool ConfirmedByUser)
    {
        public ApprovalConfirmation Validate()
        {
            if (AuthorizedMaximumMinorUnits < 0)
            {
                throw new InvalidOperationException("Google generation authorized spend ceiling cannot be negative.");
            }

            return this;
        }
    }
}
