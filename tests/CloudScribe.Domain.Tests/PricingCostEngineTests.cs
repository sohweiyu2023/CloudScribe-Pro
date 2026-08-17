using CloudScribe.Domain.Observability;
using CloudScribe.Domain.Pricing;

namespace CloudScribe.Domain.Tests;

public sealed class PricingCostEngineTests
{
    [Fact]
    public void DeterministicFakeMeterAppliesAllowanceAndTieredBlocks()
    {
        PricingMeterDefinition meter = FakeMeter();
        PricingEstimateRequest request = ResolvedRequest(250, CostUsageScope.AppLocal);

        CostAssessment result = PricingCostEngine.Estimate(meter, request);

        Assert.Equal(CostEvidenceKind.Estimated, result.EvidenceKind);
        Assert.Equal(30, result.Minimum!.Value.Units);
        Assert.Equal(30, result.Maximum!.Value.Units);
        Assert.True(result.IsApprovalSafeEstimate);
    }

    [Fact]
    public void AccountWideRequestDoesNotConsumeAppLocalAllowance()
    {
        PricingMeterDefinition meter = FakeMeter();
        PricingEstimateRequest request = ResolvedRequest(250, CostUsageScope.AccountWide);

        CostAssessment result = PricingCostEngine.Estimate(meter, request);

        Assert.Equal(50, result.Minimum!.Value.Units);
    }

    [Fact]
    public void RegionModifierProducesConservativeRangeWhenRatioNeedsRounding()
    {
        PricingMeterDefinition meter = new(
            "fixture-meter",
            "characters",
            [new PricingTier(null, 100, new ExactMoney(1, 2, "USD"))],
            modifiers: [new PricingModifier("region-adjustment", 3, 2, "region-a")]);
        PricingEstimateRequest request = ResolvedRequest(100, CostUsageScope.AppLocal, "region-a");

        CostAssessment result = PricingCostEngine.Estimate(meter, request);

        Assert.Equal(1, result.Minimum!.Value.Units);
        Assert.Equal(2, result.Maximum!.Value.Units);
    }

    [Fact]
    public void RegionModifierDoesNotApplyToAnotherRegion()
    {
        PricingMeterDefinition meter = new(
            "fixture-meter",
            "characters",
            [new PricingTier(null, 100, new ExactMoney(7, 2, "USD"))],
            modifiers: [new PricingModifier("region-adjustment", 2, 1, "region-a")]);
        PricingEstimateRequest request = ResolvedRequest(100, CostUsageScope.AppLocal, "region-b");

        CostAssessment result = PricingCostEngine.Estimate(meter, request);

        Assert.Equal(7, result.Minimum!.Value.Units);
        Assert.Equal(7, result.Maximum!.Value.Units);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void UnresolvedTaxCreditOrFxNeverProducesPretendAmount(bool tax, bool credits, bool fx)
    {
        PricingEstimateRequest request = new(
            100,
            "characters",
            CostUsageScope.AppLocal,
            "catalog:fixture",
            taxResolved: tax,
            creditsResolved: credits,
            foreignExchangeResolved: fx);

        CostAssessment result = PricingCostEngine.Estimate(FakeMeter(), request);

        Assert.Equal(CostEvidenceKind.Unknown, result.EvidenceKind);
        Assert.False(result.HasKnownAmount);
    }

    [Fact]
    public void MismatchedMeterUnitFailsClosed()
    {
        PricingEstimateRequest request = new(
            100,
            "seconds",
            CostUsageScope.AppLocal,
            "catalog:fixture",
            taxResolved: true,
            creditsResolved: true,
            foreignExchangeResolved: true);

        CostAssessment result = PricingCostEngine.Estimate(FakeMeter(), request);

        Assert.Equal(CostEvidenceKind.Unknown, result.EvidenceKind);
        Assert.Contains("does not match", result.StatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleOrConflictingCatalogIsNeverApprovalSafe()
    {
        PricingEstimateRequest request = new(
            100,
            "characters",
            CostUsageScope.AppLocal,
            "catalog:fixture",
            taxResolved: true,
            creditsResolved: true,
            foreignExchangeResolved: true,
            catalogIsStale: true,
            catalogIsConflicting: true);

        CostAssessment result = PricingCostEngine.Estimate(FakeMeter(), request);

        Assert.True(result.HasKnownAmount);
        Assert.False(result.IsApprovalSafeEstimate);
        Assert.True(result.IsStale);
        Assert.True(result.IsConflicting);
    }

    [Fact]
    public void MeterRejectsMixedCurrencyAndMissingOpenEndedFinalTier()
    {
        Assert.Throws<ArgumentException>(() => new PricingMeterDefinition(
            "fixture-meter",
            "characters",
            [new PricingTier(100, 100, new ExactMoney(1, 2, "USD"))]));
        Assert.Throws<ArgumentException>(() => new PricingMeterDefinition(
            "fixture-meter",
            "characters",
            [
                new PricingTier(100, 100, new ExactMoney(1, 2, "USD")),
                new PricingTier(null, 100, new ExactMoney(1, 2, "SGD")),
            ]));
    }

    [Fact]
    public void StableIdentifiersRejectAmbiguousOrDisplayTextTokens()
    {
        Assert.Throws<ArgumentException>(() => new PricingMeterDefinition(
            "Fixture Meter",
            "characters",
            [new PricingTier(null, 1, new ExactMoney(1, 2, "USD"))]));
        Assert.Throws<ArgumentException>(() => new PricingModifier("region modifier", 1, 1));
    }

    private static PricingMeterDefinition FakeMeter() => new(
        "fixture-meter",
        "characters",
        [
            new PricingTier(100, 100, new ExactMoney(10, 2, "USD")),
            new PricingTier(null, 100, new ExactMoney(20, 2, "USD")),
        ],
        new PricingAllowance(100, CostUsageScope.AppLocal));

    private static PricingEstimateRequest ResolvedRequest(
        long quantity,
        CostUsageScope scope,
        string? regionId = null) => new(
        quantity,
        "characters",
        scope,
        "catalog:fixture",
        regionId,
        taxResolved: true,
        creditsResolved: true,
        foreignExchangeResolved: true);
}
