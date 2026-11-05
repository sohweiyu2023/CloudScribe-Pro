using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class VoiceCatalogRestrictedRefreshTests
{
    [Fact]
    public void OrdinaryCancellationStillOffersAnExplicitRetry()
    {
        string message = VoiceCatalogErrorMessages.For(new OperationCanceledException("private"),
            GoogleCatalogRefreshPhase.CatalogRead);
        Assert.Contains("Refresh real Google voices to retry", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictedCancellationRequiresReconciliationInsteadOfAnImpossibleRetry()
    {
        string message = VoiceCatalogErrorMessages.For(new OperationCanceledException("private"),
            GoogleCatalogRefreshPhase.CatalogRead, restrictedRefresh: true);
        Assert.Contains("Review its recorded state", message, StringComparison.Ordinal);
        Assert.Contains("CatalogRead", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh real Google voices to retry", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictedTransportFailureDoesNotOfferUnconditionalRetry()
    {
        string message = VoiceCatalogErrorMessages.For(new HttpRequestException("private"),
            GoogleCatalogRefreshPhase.CatalogRequest, restrictedRefresh: true);
        Assert.Contains("no automatic retry", message, StringComparison.Ordinal);
        Assert.DoesNotContain("try Refresh real Google voices", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", message, StringComparison.Ordinal);
    }
}
