using CloudScribe.Domain.Observability;
using CloudScribe.Domain.Pricing;

namespace CloudScribe.Domain.Tests;

public sealed class CostAssessmentTests
{
    [Fact]
    public void UnknownCostCannotPretendToBeApprovalSafe()
    {
        CostAssessment cost = CostAssessment.Unknown(CostUsageScope.AppLocal, "No admitted pricing meter matches this selection.");
        Assert.Equal(CostEvidenceKind.Unknown, cost.EvidenceKind);
        Assert.False(cost.HasKnownAmount);
        Assert.False(cost.IsApprovalSafeEstimate);
        Assert.Null(cost.ProvenanceId);
    }

    [Fact]
    public void EstimatedRangePreservesExactIntegerMoneyAndUncertainty()
    {
        CostAssessment cost = CostAssessment.Estimate(
            new ExactMoney(120, 4, "USD"),
            new ExactMoney(180, 4, "USD"),
            CostUsageScope.AppLocal,
            "catalog:fixture-v1",
            isStale: true);
        Assert.Equal(120, cost.Minimum!.Value.Units);
        Assert.Equal(180, cost.Maximum!.Value.Units);
        Assert.True(cost.IsStale);
        Assert.False(cost.IsApprovalSafeEstimate);
    }

    [Fact]
    public void RejectsMixedCurrencyOrReversedRanges()
    {
        Assert.Throws<ArgumentException>(() => CostAssessment.Estimate(
            new ExactMoney(1, 2, "USD"), new ExactMoney(2, 2, "SGD"), CostUsageScope.AppLocal, "fixture"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CostAssessment.Estimate(
            new ExactMoney(3, 2, "USD"), new ExactMoney(2, 2, "USD"), CostUsageScope.AppLocal, "fixture"));
    }

    [Fact]
    public void QuotedProviderAndInvoiceStatesRemainDistinct()
    {
        ExactMoney amount = new(2500, 4, "USD");
        Assert.Equal(CostEvidenceKind.Quoted, CostAssessment.Quoted(amount, CostUsageScope.AppLocal, "quote:1").EvidenceKind);
        Assert.Equal(CostEvidenceKind.ProviderReported, CostAssessment.ProviderReported(amount, CostUsageScope.AccountWide, "provider:1").EvidenceKind);
        Assert.Equal(CostEvidenceKind.ReconciledInvoice, CostAssessment.ReconciledInvoice(amount, CostUsageScope.AccountWide, "invoice:1").EvidenceKind);
    }
}
