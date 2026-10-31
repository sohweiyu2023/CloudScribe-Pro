using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("https://texttospeech.googleapis.com")]
    [InlineData("https://texttospeech.googleapis.com/")]
    [InlineData("https://TEXTTOSPEECH.googleapis.com")]
    [InlineData("https://texttospeech.googleapis.com:443/")]
    public async Task ReservedResumeAcceptsEquivalentProductionOriginFormsWithoutNewFunds(string origin)
    {
        using var f = new Fixture();
        var r = await ReservedResumeAsync(f);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE google_generation_project_authorizations SET EndpointOrigin={0} WHERE AccountId={1} AND ProjectId={2} AND ModelId={3}",
                [origin, r.Intent.Envelope.AccountId, r.Intent.ProjectId, r.Intent.Envelope.VoiceName], TestContext.Current.CancellationToken);

        string before = await CatalogRowsAsync(f);
        var current = await r.Old.Service.InspectReservedResumeAdmissionAsync(r.Reserved.AuthorizationId, r.Old.Admission.Policy!,
            r.Admission.Policy!.CatalogSnapshotId, r.Admission.Policy.CatalogExpiresAtUtc, TestContext.Current.CancellationToken);
        Assert.False(current.GrantsAuthority);
        r.Admission.Policy = r.Admission.Policy with { ProviderEvidenceSha256 = current.ProviderEvidenceSha256 };
        var review = await ResumeReviewAsync(r);
        Assert.True(review.IsCatalogRevalidation);
        Assert.Equal(r.Reserved.AuthorizationId, review.ExistingAuthorizationId);
        await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var final = f.CreateDbContext();
        Assert.Equal(1, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await final.Database.SqlQueryRaw<int>("SELECT SUM(Consumed) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://texttospeech.googleapis.com/")]
    [InlineData("https://other.googleapis.com/")]
    [InlineData("https://texttospeech.googleapis.com:444/")]
    [InlineData("https://user@texttospeech.googleapis.com/")]
    [InlineData("https://texttospeech.googleapis.com/v1")]
    [InlineData("https://texttospeech.googleapis.com/?query=value")]
    [InlineData("https://texttospeech.googleapis.com/#fragment")]
    [InlineData("not-an-origin")]
    public async Task ReservedResumeStillRejectsDifferentOrMalformedOriginsWithoutMapping(string origin)
    {
        using var f = new Fixture();
        var r = await ReservedResumeAsync(f);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE google_generation_project_authorizations SET EndpointOrigin={0} WHERE AccountId={1} AND ProjectId={2} AND ModelId={3}",
                [origin, r.Intent.Envelope.AccountId, r.Intent.ProjectId, r.Intent.Envelope.VoiceName], TestContext.Current.CancellationToken);
        string before = await CatalogRowsAsync(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Old.Service.InspectReservedResumeAdmissionAsync(
            r.Reserved.AuthorizationId, r.Old.Admission.Policy!, r.Admission.Policy!.CatalogSnapshotId,
            r.Admission.Policy.CatalogExpiresAtUtc, TestContext.Current.CancellationToken));
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var final = f.CreateDbContext();
        Assert.Equal(0, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
    }
}
