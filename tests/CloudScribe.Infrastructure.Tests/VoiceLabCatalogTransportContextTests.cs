using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabCatalogTransportContextTests
{
    private static readonly VoiceLabCatalogQuery Query = new(
        ProviderId: "google",
        AccountId: "account-1",
        ProjectId: "project-1",
        SearchText: null,
        Locale: "en-US",
        IncludePrivateVoices: false);

    [Theory]
    [InlineData(" credential-1")]
    [InlineData("credential-1 ")]
    [InlineData("credential\r1")]
    [InlineData("credential\n1")]
    public void ConstructorRejectsNonCanonicalCredentialReference(string credentialReferenceId)
    {
        Assert.Throws<InvalidOperationException>(() => new VoiceLabCatalogTransportContext(
            Query,
            credentialReferenceId,
            "capability-1",
            new Uri("https://texttospeech.googleapis.com")));
    }

    [Theory]
    [InlineData(" capability-1")]
    [InlineData("capability-1 ")]
    [InlineData("capability\r1")]
    [InlineData("capability\n1")]
    public void ConstructorRejectsNonCanonicalCapabilityEvidence(string capabilityEvidenceId)
    {
        Assert.Throws<InvalidOperationException>(() => new VoiceLabCatalogTransportContext(
            Query,
            "credential-1",
            capabilityEvidenceId,
            new Uri("https://texttospeech.googleapis.com")));
    }

    [Theory]
    [InlineData("http://texttospeech.googleapis.com")]
    [InlineData("https://user@texttospeech.googleapis.com")]
    [InlineData("https://texttospeech.googleapis.com/v1")]
    [InlineData("https://texttospeech.googleapis.com/?query=1")]
    [InlineData("https://texttospeech.googleapis.com/#fragment")]
    public void ConstructorRejectsEndpointThatIsNotAnExplicitHttpsOrigin(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => new VoiceLabCatalogTransportContext(
            Query,
            "credential-1",
            "capability-1",
            new Uri(endpoint)));
    }

    [Fact]
    public void ConstructorPreservesTrustedBoundContext()
    {
        Uri endpoint = new("https://texttospeech.googleapis.com");

        VoiceLabCatalogTransportContext context = new(
            Query,
            "credential-1",
            "capability-1",
            endpoint);

        Assert.Same(Query, context.Query);
        Assert.Equal("credential-1", context.CredentialReferenceId);
        Assert.Equal("capability-1", context.CapabilityEvidenceId);
        Assert.Equal(endpoint, context.EndpointOrigin);
    }
}
