using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task RefinedCompleteArticleUsesExistingExportAndPreservesOrderedAcceptedAudio()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var fixture = await RefinedEvidenceAsync(f);
        var parts = fixture.Article.Proposal.Parts;
        var article = fixture.Article with { AllowedSegments = parts.Where(x => x.Range.ReusedReceiptSha256 is null).Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray() };
        fixture.Evidence.Admission.Policy = fixture.Evidence.Admission.Policy! with
        {
            Articles = [article],
            MaximumNewAttempts = article.AllowedSegments.Count,
            MaximumAdditionalReservationMinorUnits = article.AllowedSegments.Count
        };
        var service = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        var evidence = fixture.Evidence with { Article = article };
        var native = new ChirpArticleNative();
        var exporter = new ArticleAudioExportService(f, f.Library, new GoogleGenerationPersistedQueueStateStore(f), f.Paths, native, chirpArticles: service);
        string destination = Path.Combine(f.Root, "refined-complete.mp3"), tool = CreateArticleTools(f.Root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(evidence.Document.Id, destination, tool, TestContext.Current.CancellationToken));
        string oldSpend = JsonHash(await FinancialRowsAsync(f, "google_generation_spend_authorizations"));
        foreach (var permission in article.AllowedSegments)
        {
            var accepted = await AcceptChirpAsync(f, service, ChirpIntent(evidence, permission.CoverageOrdinal), permission.CoverageOrdinal);
            await service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        }
        fixture.Evidence.Admission.Policy = null; fixture.Evidence.Clock.Now = fixture.Evidence.Clock.Now.AddHours(2);
        var restarted = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        Assert.True((await restarted.ProgressAsync(evidence.Document.Id, TestContext.Current.CancellationToken)).Complete);
        await exporter.ExportAsync(evidence.Document.Id, destination, tool, TestContext.Current.CancellationToken);
        Assert.Equal(parts.Select(x => x.Range.ReusedReceiptSha256 is not null ? (byte)2 : checked((byte)x.CoverageOrdinal)), native.InputMarkers);
        Assert.True(File.Exists(destination));
        Assert.Equal(fixture.AcceptedBytes, await File.ReadAllBytesAsync(fixture.Reuse.Audio.AudioPath, TestContext.Current.CancellationToken));
        Assert.Equal(oldSpend, JsonHash(await FinancialRowsAsync(f, "google_generation_spend_authorizations")), StringComparer.Ordinal);
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_plans")).Count);
        Assert.Equal(article.AllowedSegments.Count + 2, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(evidence.Document.Id, destination, tool, TestContext.Current.CancellationToken));
    }
}
