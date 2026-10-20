using System.Net;
using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class VoiceCatalogErrorMessagesTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "saved connection")]
    [InlineData(HttpStatusCode.Forbidden, "billing")]
    [InlineData(HttpStatusCode.BadGateway, "internet connection")]
    public void NetworkErrorsOfferActionWithoutProviderSecrets(HttpStatusCode code, string action)
    {
        string message = VoiceCatalogErrorMessages.For(new HttpRequestException("private-key-and-token", null, code));
        Assert.Contains(action, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-key-and-token", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnexpectedErrorsNeverEchoPrivateDetails()
    {
        string message = VoiceCatalogErrorMessages.For(new InvalidOperationException("credential-secret personal-project"));
        Assert.Contains("Setups", message, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("personal-project", message, StringComparison.Ordinal);
    }
}
