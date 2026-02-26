using CloudReader.Core.Costing;

namespace CloudReader.Tests;

public sealed class CostEstimatorTests
{
    [Fact]
    public void Estimate_ChargesOnlyDeltaBeyondFreeTier()
    {
        var estimator = new CostEstimator();
        var cost = estimator.Estimate("Neural2", 500_000, 900_000);
        Assert.Equal(6.4m, cost);
    }
}
