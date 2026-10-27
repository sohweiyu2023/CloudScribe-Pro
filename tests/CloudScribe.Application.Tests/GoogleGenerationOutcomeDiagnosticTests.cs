using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class GoogleGenerationOutcomeDiagnosticTests
{
    [Theory]
    [InlineData("google-http-400")]
    [InlineData("google-http-403")]
    [InlineData("google-http-408")]
    [InlineData("google-http-429")]
    [InlineData("google-http-503")]
    [InlineData("google-transport-timeout")]
    public void RetainsOnlyRecognizedStatusMetadata(string code) =>
        Assert.Equal(code, GoogleGenerationOutcomeDiagnostic.Sanitize(code));

    [Theory]
    [InlineData(null)]
    [InlineData("google-generation-authorized")]
    [InlineData("google-http-403\nAuthorization: SECRET")]
    [InlineData("google-http-999")]
    [InlineData("{\"error\":\"private document text\"}")]
    public void DropsArbitraryProviderOrAdmissionText(string? code) =>
        Assert.Equal("google-outcome-unavailable", GoogleGenerationOutcomeDiagnostic.Sanitize(code));
}
