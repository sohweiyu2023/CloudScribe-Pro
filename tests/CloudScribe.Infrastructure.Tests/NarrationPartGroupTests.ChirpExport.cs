using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ChirpTwoContinuousArticlesReuseAcceptedAudioAndPreserveLibraryAndFinancialHistory()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var first = await ChirpEvidenceAsync(f, 10); var reuse = await AddAcceptedChirpReuseAsync(f, first); first = reuse.Evidence;
        byte[] original = await File.ReadAllBytesAsync(reuse.Original, TestContext.Current.CancellationToken);
        var second = await ChirpEvidenceAsync(f, 10);
        var articles = new[] { first.Article, second.Article };
        int requests = articles.Sum(x => x.AllowedSegments.Count);
        second.Admission.Policy = second.Admission.Policy! with { Articles = articles, MaximumNewAttempts = requests, MaximumAdditionalReservationMinorUnits = requests };
        using var before = f.CreateDbContext(); int documents = await before.Documents.CountAsync(TestContext.Current.CancellationToken);
        string spendBefore = JsonHash(await FinancialRowsAsync(f, "google_generation_spend_authorizations"));
        var queue = new GoogleGenerationPersistedQueueStateStore(f);
        string tool = CreateArticleTools(f.Root);
        foreach (var evidence in new[] { first, second })
        {
            var native = new ChirpArticleNative();
            var exporter = new ArticleAudioExportService(f, f.Library, queue, f.Paths, native, chirpArticles: second.Service);
            string destination = Path.Combine(f.Root, evidence.Document.Id.ToString("N") + ".mp3");
            await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(evidence.Document.Id, destination, tool, TestContext.Current.CancellationToken));
            foreach (var part in evidence.Article.Proposal.Parts.Where(x => x.Range.ReusedReceiptSha256 is null))
            {
                var accepted = await AcceptChirpAsync(f, second.Service, ChirpIntent(evidence, part.CoverageOrdinal), part.CoverageOrdinal);
                await second.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
            }
            Assert.True((await second.Service.ProgressAsync(evidence.Document.Id, TestContext.Current.CancellationToken)).Complete);
            await exporter.ExportAsync(evidence.Document.Id, destination, tool, TestContext.Current.CancellationToken);
            Assert.Equal(Enumerable.Range(1, evidence.Article.Proposal.Parts.Count).Select(x => (byte)x), native.InputMarkers);
            Assert.True(File.Exists(destination));
            await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(evidence.Document.Id, destination, tool, TestContext.Current.CancellationToken));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(reuse.Original, TestContext.Current.CancellationToken));
        Assert.Equal(spendBefore, JsonHash(await FinancialRowsAsync(f, "google_generation_spend_authorizations")));
        using var after = f.CreateDbContext(); Assert.Equal(documents, await after.Documents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(requests, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        Assert.Equal(requests, (await FinancialRowsAsync(f, "chirp_article_receipts")).Count);
    }

    private sealed class ChirpArticleNative : INativeMediaTool
    {
        public List<byte> InputMarkers { get; } = [];
        public async Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            string output = string.Empty;
            if (invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal))
                output = JsonSerializer.Serialize(new
                {
                    streams = new[] { new { codec_name = "mp3", sample_rate = "24000", channels = 1 } },
                    format = new { duration = Path.GetFileName(invocation.Arguments[^1]).StartsWith("input-", StringComparison.Ordinal) ? "1" : Math.Max(InputMarkers.Count, 1).ToString(CultureInfo.InvariantCulture) }
                });
            else if (invocation.Arguments.Contains("-filter_complex", StringComparer.Ordinal))
            {
                for (int i = 0; i < invocation.Arguments.Count; i++)
                    if (string.Equals(invocation.Arguments[i], "-i", StringComparison.Ordinal)) InputMarkers.Add((await File.ReadAllBytesAsync(invocation.Arguments[i + 1], cancellationToken).ConfigureAwait(false))[10]);
                byte[] bytes = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(bytes, 0);
                await File.WriteAllBytesAsync(invocation.Arguments[^1], bytes, cancellationToken).ConfigureAwait(false);
            }
            return new(0, false, output, string.Empty, TimeSpan.Zero); // Fake media fixture, not decoded/heard audio proof.
        }
    }
}
