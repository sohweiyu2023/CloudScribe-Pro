using CloudScribe.Domain.Observability;

namespace CloudScribe.Domain.Tests;

public sealed class BillableOperationLedgerEntryTests
{
    [Fact]
    public void DirectConstructionCannotBypassLedgerInvariants()
    {
        DateTimeOffset utc = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        ExactMoney amount = new(125, 2, "SGD");

        Assert.Throws<ArgumentException>(() => new BillableOperationLedgerEntry(
            Guid.Empty,
            Guid.NewGuid(),
            "snapshot",
            BillableLedgerEventKind.EstimateApproved,
            utc,
            amount,
            "correlation",
            null,
            "APPROVED"));
        Assert.Throws<ArgumentException>(() => new BillableOperationLedgerEntry(
            Guid.NewGuid(),
            Guid.Empty,
            "snapshot",
            BillableLedgerEventKind.EstimateApproved,
            utc,
            amount,
            "correlation",
            null,
            "APPROVED"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BillableOperationLedgerEntry(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "snapshot",
            (BillableLedgerEventKind)999,
            utc,
            amount,
            "correlation",
            null,
            "APPROVED"));
        Assert.Throws<ArgumentException>(() => new BillableOperationLedgerEntry(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "snapshot",
            BillableLedgerEventKind.EstimateApproved,
            utc.ToOffset(TimeSpan.FromHours(8)),
            amount,
            "correlation",
            null,
            "APPROVED"));
    }
}
