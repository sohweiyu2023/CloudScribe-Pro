using System.Collections;
using System.Net;
using CloudScribe.App.Composition;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

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
    [Fact]
    public async Task ProbeHttpFailureShowsSafeStatusAndRealStageWithoutProviderText()
    {
        using var client = new HttpClient(new ForbiddenProbe());
        var probe = new GoogleSynthesisCapabilityProbe(client, new Token(), TimeProvider.System);
        var account = new ProviderAccountReference(GoogleGenerationProvider.StableProviderId, "synthetic", "Synthetic",
            new CredentialReference("synthetic-credential"), endpointOrigin: new Uri("https://texttospeech.googleapis.com"));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.VerifyAsync(account,
            "synthetic-project", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), TestContext.Current.CancellationToken));

        string message = VoiceCatalogErrorMessages.For(failure, GoogleCatalogRefreshPhase.UiBootstrap);

        Assert.Contains("HTTP 403", message, StringComparison.Ordinal);
        Assert.Contains("ProbeValidation", message, StringComparison.Ordinal);
        Assert.Contains("billing", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-body", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArbitraryExceptionDataDoesNotBecomeTrustedDiagnosticContext()
    {
        var failure = new InvalidOperationException("secret-token");
        failure.Data["HttpStatus"] = 403;
        failure.Data["CatalogPhase"] = "private-account-name";
        string message = VoiceCatalogErrorMessages.For(failure, GoogleCatalogRefreshPhase.UiQuery);
        Assert.Contains("UiQuery", message, StringComparison.Ordinal);
        Assert.DoesNotContain("403", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-account-name", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLocalConnectionGuidesRecoveryWithoutSuggestingKeyReplacement()
    {
        var service = new GoogleServiceAccountCredentialOnboardingService(new MissingVault());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveVoiceCatalogEndpointAsync("synthetic", TestContext.Current.CancellationToken));
        string message = VoiceCatalogErrorMessages.For(error, GoogleCatalogRefreshPhase.UiBootstrap);
        Assert.Contains("local credential material is unavailable", message, StringComparison.Ordinal);
        Assert.Contains("saved-credential-unavailable", message, StringComparison.Ordinal);
        Assert.Contains("no key was changed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenExceptionMetadataCannotBreakSafeErrorPresentation()
    {
        string message = VoiceCatalogErrorMessages.For(new BrokenData(), GoogleCatalogRefreshPhase.UiQuery);
        Assert.Contains("unexpected", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-data", message, StringComparison.Ordinal);
    }

    private sealed class BrokenData : Exception
    {
        public override IDictionary Data => throw new InvalidOperationException("private-data");
    }
    private sealed class MissingVault : ICredentialVault
    {
        public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken token = default) => ValueTask.FromResult<CredentialSecret?>(null);
        public ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class ForbiddenProbe : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private-response-body") });
    }
    private sealed class Token : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult("synthetic-token");
    }
}
