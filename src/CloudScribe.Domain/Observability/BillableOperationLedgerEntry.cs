namespace CloudScribe.Domain.Observability;

public sealed record BillableOperationLedgerEntry
{
    public BillableOperationLedgerEntry(
        Guid id,
        Guid operationId,
        string snapshotId,
        BillableLedgerEventKind eventKind,
        DateTimeOffset occurredAtUtc,
        ExactMoney amount,
        string correlationId,
        string? providerRequestId,
        string eventCode)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entry ID is required.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation ID is required.", nameof(operationId));
        }

        if (!Enum.IsDefined(eventKind))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Ledger instants must be expressed in UTC.", nameof(occurredAtUtc));
        }

        amount.EnsureValid(nameof(amount));

        Id = id;
        OperationId = operationId;
        SnapshotId = RequiredToken(snapshotId, 128);
        EventKind = eventKind;
        OccurredAtUtc = occurredAtUtc;
        Amount = amount;
        CorrelationId = RequiredToken(correlationId, 96);
        ProviderRequestId = OptionalToken(providerRequestId, 160);
        EventCode = RequiredToken(eventCode, 80);
    }

    public Guid Id { get; }

    public Guid OperationId { get; }

    public string SnapshotId { get; }

    public BillableLedgerEventKind EventKind { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public ExactMoney Amount { get; }

    public string CorrelationId { get; }

    public string? ProviderRequestId { get; }

    public string EventCode { get; }

    public static BillableOperationLedgerEntry Create(
        TimeProvider timeProvider,
        Guid operationId,
        string snapshotId,
        BillableLedgerEventKind eventKind,
        ExactMoney amount,
        string correlationId,
        string? providerRequestId,
        string eventCode)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new BillableOperationLedgerEntry(
            Guid.NewGuid(),
            operationId,
            snapshotId,
            eventKind,
            timeProvider.GetUtcNow(),
            amount,
            correlationId,
            providerRequestId,
            eventCode);
    }

    private static string RequiredToken(string value, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return OptionalToken(value, maximumLength)!;
    }

    private static string? OptionalToken(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Value exceeds {maximumLength} characters.");
        }

        return normalized;
    }
}
