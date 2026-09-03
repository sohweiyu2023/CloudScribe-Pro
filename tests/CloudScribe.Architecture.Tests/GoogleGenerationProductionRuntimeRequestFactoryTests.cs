using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionRuntimeRequestFactoryTests
{
    [Fact]
    public void Create_RejectsMissingAuthorization()
    {
        var factory = new GoogleGenerationProductionRuntimeRequestFactory();

        Assert.Throws<ArgumentNullException>(() => factory.Create(null!, null!, 0));
    }

    [Fact]
    public void Create_RejectsEstimateDriftBeforeUsingTheRuntimeSnapshot()
    {
        var envelope = new GoogleGenerationSubmissionEnvelope(
            "google-account",
            "credential-ref",
            "capability-v1",
            "pricing-v1",
            12,
            "en-US-Studio-O",
            "MP3",
            "00",
            1);
        GoogleGenerationSpendAuthorization authorization = GoogleGenerationSpendAuthorization.Create(
            envelope,
            "USD",
            2,
            approvedEstimateMinorUnits: 125,
            authorizedMaximumMinorUnits: 150);
        var incompleteSnapshot = new GoogleGenerationUiExecutionSnapshot(
            null!,
            false,
            false,
            false,
            false,
            null!,
            null!,
            null!,
            null!,
            null!,
            false,
            false,
            false,
            false);
        var factory = new GoogleGenerationProductionRuntimeRequestFactory();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => factory.Create(authorization, incompleteSnapshot, 126));

        Assert.Contains("estimate changed after approval", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
