using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionRuntimeRequestFactoryTests
{
    [Fact]
    public void CreateRejectsMissingAuthorization()
    {
        Assert.Throws<ArgumentNullException>(
            () => GoogleGenerationProductionRuntimeRequestFactory.Create(null!, null!, 0));
    }

    [Fact]
    public void CreateRejectsEstimateDriftBeforeUsingTheRuntimeSnapshot()
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
            GoogleGenerationReconciliationResolutionEvidence.None,
            false,
            false,
            false,
            false);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => GoogleGenerationProductionRuntimeRequestFactory.Create(authorization, incompleteSnapshot, 126));

        Assert.Contains("estimate changed after approval", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
