namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class BillableLedgerEntity
{
    public Guid Id { get; set; }

    public Guid OperationId { get; set; }

    public required string SnapshotId { get; set; }

    public int EventKind { get; set; }

    public long OccurredAtUnixMilliseconds { get; set; }

    public long AmountUnits { get; set; }

    public int AmountScale { get; set; }

    public required string CurrencyCode { get; set; }

    public required string CorrelationId { get; set; }

    public string? ProviderRequestId { get; set; }

    public required string EventCode { get; set; }
}
