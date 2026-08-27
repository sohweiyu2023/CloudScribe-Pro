using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleGenerationSpendAuthorizationTests
{
    [Fact]
    public void ExactIdentityCurrencyAndEstimateRemainAuthorized()
    {
        var authorization = GoogleGenerationSpendAuthorization.Create(Envelope(), "usd", 6, 125_000, 150_000);

        authorization.EnsureStillAuthorized(Envelope(), "USD", 6, 125_000);

        Assert.Equal("USD", authorization.Currency);
        Assert.Equal(150_000, authorization.AuthorizedMaximumMinorUnits);
    }

    [Fact]
    public void EstimateCurrencyAndEnvelopeDriftFailClosed()
    {
        var authorization = GoogleGenerationSpendAuthorization.Create(Envelope(), "USD", 6, 125_000, 150_000);

        Assert.Throws<InvalidOperationException>(() => authorization.EnsureStillAuthorized(Envelope(), "EUR", 6, 125_000));
        Assert.Throws<InvalidOperationException>(() => authorization.EnsureStillAuthorized(Envelope(), "USD", 6, 126_000));
        Assert.Throws<InvalidOperationException>(() => authorization.EnsureStillAuthorized(Envelope() with { RequestRevision = 8 }, "USD", 6, 125_000));
    }

    [Fact]
    public void ApprovalCannotBeCreatedAboveCeiling()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GoogleGenerationSpendAuthorization.Create(Envelope(), "USD", 6, 151_000, 150_000));
    }

    private static GoogleGenerationSubmissionEnvelope Envelope() =>
        new(
            "account-1",
            "credential-ref-1",
            "capability-v1",
            "pricing-v1",
            7,
            "voice-a",
            "LINEAR16",
            new string('a', 64),
            128);
}
