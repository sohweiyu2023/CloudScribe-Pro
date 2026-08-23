using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage6GoogleBillableExecutionGateTests
{
    [Fact]
    public void ExactImmutableRequestRemainsAuthorized()
    {
        var authorization = CreateAuthorization();

        authorization.EnsureExecutionStillAuthorized(
            "acct-google-1",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "USD",
            6,
            1250,
            7);
    }

    [Fact]
    public void PayloadOrPricingDriftBlocksBillableExecution()
    {
        var authorization = CreateAuthorization();

        Assert.Throws<InvalidOperationException>(() => authorization.EnsureExecutionStillAuthorized(
            "acct-google-1",
            new string('a', 64),
            new string('d', 64),
            new string('c', 64),
            "USD",
            6,
            1250,
            7));

        Assert.Throws<InvalidOperationException>(() => authorization.EnsureExecutionStillAuthorized(
            "acct-google-1",
            new string('a', 64),
            new string('b', 64),
            new string('e', 64),
            "USD",
            6,
            1250,
            7));
    }

    [Fact]
    public void EstimateIncreaseBeyondApprovalOrCeilingBlocksExecution()
    {
        var authorization = CreateAuthorization();

        Assert.Throws<InvalidOperationException>(() => authorization.EnsureExecutionStillAuthorized(
            "acct-google-1",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "USD",
            6,
            1300,
            7));

        Assert.Throws<InvalidOperationException>(() => GoogleBillableExecutionAuthorization.Create(
            "acct-google-1",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "USD",
            6,
            1500,
            1501,
            7));
    }

    private static GoogleBillableExecutionAuthorization CreateAuthorization() =>
        GoogleBillableExecutionAuthorization.Create(
            "acct-google-1",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "USD",
            6,
            1500,
            1250,
            7);
}
