using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleConnectionSetupErrorMessagesTests
{
    private const string SensitiveDetails = "private-key test-token private-project";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "service-account key")]
    [InlineData(HttpStatusCode.Forbidden, "billing")]
    [InlineData(HttpStatusCode.BadGateway, "internet connection")]
    public void ProviderFailuresOfferRecoveryWithoutEchoingTheResponse(HttpStatusCode code, string recovery)
    {
        AssertSafeRecovery(new HttpRequestException(SensitiveDetails, null, code), recovery);
    }

    [Fact]
    public void StoppedSetupAdvisesCheckingSavedStateBeforeRetrying()
    {
        AssertSafeRecovery(new OperationCanceledException(SensitiveDetails), "inspect Setups before trying again");
    }

    [Fact]
    public void InvalidJsonNeverEchoesCredentialParserDetails()
    {
        AssertSafeRecovery(new JsonException(SensitiveDetails), "complete downloaded JSON key file");
        AssertSafeRecovery(new FormatException(SensitiveDetails), "complete downloaded JSON key file");
        AssertSafeRecovery(new CryptographicException(SensitiveDetails), "complete downloaded JSON key file");
    }

    [Fact]
    public void UnexpectedFailureDoesNotClaimAPartiallySavedConnectionWasRolledBack()
    {
        string message = AssertSafeRecovery(new InvalidOperationException(SensitiveDetails), "Inspect saved connections in Setups");
        Assert.DoesNotContain("unchanged", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not changed", message, StringComparison.OrdinalIgnoreCase);
    }

    private static string AssertSafeRecovery(Exception exception, string recovery)
    {
        string message = GoogleConnectionSetupErrorMessages.For(exception);
        Assert.Contains(recovery, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No speech was requested", message, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveDetails, message, StringComparison.Ordinal);
        return message;
    }
}
