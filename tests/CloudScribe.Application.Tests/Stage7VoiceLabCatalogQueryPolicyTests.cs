using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage7VoiceLabCatalogQueryPolicyTests
{
    [Fact]
    public void Authorized_public_voice_query_is_admitted()
    {
        var query = new VoiceLabCatalogQuery("provider-1", "acct-1", "project-1", "calm", "en-US", false);
        var admitted = VoiceLabCatalogQueryPolicy.RequireAuthorized(query, true, true, false);
        Assert.Same(query, admitted);
    }

    [Fact]
    public void Private_voice_query_requires_explicit_current_authorization()
    {
        var query = new VoiceLabCatalogQuery("provider-1", "acct-1", "project-1", null, null, true);
        Assert.Throws<InvalidOperationException>(() => VoiceLabCatalogQueryPolicy.RequireAuthorized(query, true, true, false));
    }

    [Fact]
    public void Noncanonical_trust_identity_fails_closed()
    {
        var query = new VoiceLabCatalogQuery("provider-1", "acct-1\n", "project-1", null, null, false);
        Assert.Throws<InvalidOperationException>(() => VoiceLabCatalogQueryPolicy.RequireAuthorized(query, true, true, false));
    }
}
