using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed class GoogleGenerationProductionSpendApprovalService
{
    private readonly GoogleGenerationProductionSubmissionStateOwner _stateOwner;

    public GoogleGenerationProductionSpendApprovalService(
        GoogleGenerationProductionSubmissionStateOwner stateOwner)
    {
        _stateOwner = stateOwner ?? throw new ArgumentNullException(nameof(stateOwner));
    }

    public async Task ApproveExplicitAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (!request.ConfirmedByUser)
        {
            throw new InvalidOperationException(
                "Google generation spend requires an explicit user confirmation for the exact compiled submission.");
        }

        GoogleGenerationSpendAuthorization authorization = GoogleGenerationSpendAuthorization.Create(
            request.Envelope,
            request.Currency,
            request.Scale,
            request.CurrentEstimateMinorUnits,
            request.AuthorizedMaximumMinorUnits);

        _ = GoogleGenerationProductionRuntimeRequestFactory.Create(
            authorization,
            request.Snapshot,
            request.CurrentEstimateMinorUnits).Validate();

        await _stateOwner.ApproveAsync(
            authorization,
            request.Snapshot,
            request.CurrentEstimateMinorUnits,
            cancellationToken).ConfigureAwait(false);
    }

    public sealed record ApprovalRequest(
        GoogleGenerationSubmissionEnvelope Envelope,
        GoogleGenerationUiExecutionSnapshot Snapshot,
        string Currency,
        int Scale,
        long CurrentEstimateMinorUnits,
        long AuthorizedMaximumMinorUnits,
        bool ConfirmedByUser)
    {
        public ApprovalRequest Validate()
        {
            if (Envelope is null)
            {
                throw new ArgumentNullException(nameof(Envelope));
            }

            if (Snapshot is null)
            {
                throw new ArgumentNullException(nameof(Snapshot));
            }

            if (string.IsNullOrWhiteSpace(Currency))
            {
                throw new ArgumentException("Currency is required.", nameof(Currency));
            }

            if (Scale is < 0 or > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(Scale));
            }

            if (CurrentEstimateMinorUnits < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(CurrentEstimateMinorUnits));
            }

            if (AuthorizedMaximumMinorUnits < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(AuthorizedMaximumMinorUnits));
            }

            if (CurrentEstimateMinorUnits > AuthorizedMaximumMinorUnits)
            {
                throw new InvalidOperationException(
                    "Google generation estimate exceeds the explicitly authorized spend ceiling.");
            }

            return this;
        }
    }
}
