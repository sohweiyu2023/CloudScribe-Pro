using CloudScribe.Domain.Observability;

namespace CloudScribe.Application.Observability;

public interface IBillableOperationLedger
{
    Task AppendRequiredAsync(BillableOperationLedgerEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillableOperationLedgerEntry>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default);
}
