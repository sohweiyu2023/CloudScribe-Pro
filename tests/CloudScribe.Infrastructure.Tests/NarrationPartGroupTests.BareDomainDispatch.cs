using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task BareDomainWholeArticleDispatchBindsSavedV2TextAndCostAcrossEveryPart()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var n = await NarrationArticleAsync(f, whole: true, bare: true); var e = n.Evidence;
        Assert.Contains("84000.co", n.Source.Text, StringComparison.Ordinal);
        Assert.Equal(NarrationUrlOmission.CurrentVersion, n.Copy.TransformVersion);
        Assert.DoesNotContain("84000.co", n.Copy.Narration.Text, StringComparison.Ordinal);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Hash(n.Copy.Narration.Text), e.Article.Proposal.SourceSha256);
        Assert.Equal(review.RemainingRequests, review.MaximumAdditionalMinorUnits);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        List<string> sent = [];
        var runtime = new WholeFakeRuntime(f, e)
        {
            ObservePayload = payload =>
            {
                using var wire = JsonDocument.Parse(payload);
                var input = wire.RootElement.GetProperty("input");
                sent.Add(input.TryGetProperty("text", out var text) ? text.GetString()!
                    : XElement.Parse(input.GetProperty("ssml").GetString()!, LoadOptions.PreserveWhitespace).Value);
            }
        };
        var result = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, result.State);
        Assert.Equal(n.Copy.Narration.Text, string.Concat(sent));
        Assert.All(sent, text => Assert.DoesNotContain("84000.co", text, StringComparison.Ordinal));
        Assert.Equal(n.Source.Text, (await f.Library.OpenAsync(n.Source.Id, TestContext.Current.CancellationToken))!.Text);
    }

    [Fact]
    public async Task BareDomainBatchLongAudioCompilesAndPostsOnlySavedV2CopyWithSeparateRawHash()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var imported = await ImportBareBatchCopyAsync(f, e);
        var copy = imported.Copy;
        var p = await PrepareLongInstalledAsync(f, e, longDocument: copy.Narration);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var job = new GoogleLongAudioBatchJob(imported.Batch.BatchId, imported.Item.ItemId, copy.Narration.Id,
            copy.Narration.CurrentRevisionId!.Value, imported.Item.SourceSha256, p.Request.AttemptId, p.Request.OutputGcsUri,
            4096, 24000, 1, p.Request.BindingSha256, 1, 1, false, NarrationTextSha256: imported.Item.NarrationTextSha256);
        var compiled = GoogleLongAudioDocumentCompiler.Compile(scope, job.ToDocumentJob(), copy.Narration.Text);
        Assert.Equal(p.Request.BindingSha256, compiled.BindingSha256);
        Assert.Equal(Hash(copy.Narration.Text), job.ToDocumentJob().SourceSha256);
        Assert.NotEqual(job.SourceSha256, job.NarrationTextSha256, StringComparer.Ordinal);
        Assert.Equal(NarrationUrlOmission.CurrentVersion, imported.Item.NarrationSource!.TransformVersion);
        var original = (await f.Library.OpenAsync(copy.SourceDocumentId, TestContext.Current.CancellationToken))!;
        Assert.Throws<InvalidDataException>(() => GoogleLongAudioDocumentCompiler.Compile(scope, job.ToDocumentJob(), original.Text));
        var transport = new LongRuntimeTransport(compiled);
        string? posted = null;
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, compiled, () => new CopyLongAudioHandler(transport, text => posted = text));
        using var session = await fixture.Factory.OpenAsync(compiled, fixture.Tools,
            GoogleLongAudioDocumentCompiler.OutputAllowance(scope, job.ToDocumentJob()), true, TestContext.Current.CancellationToken);
        Assert.Equal(compiled.BindingSha256, session.Review.RequestBindingSha256);
        Assert.Equal(Encoding.UTF8.GetByteCount(copy.Narration.Text), session.Review.InputUtf8Bytes);
        Assert.Equal(copy.Narration.Text.Length, session.Review.ConservativeCharacterUnits);
        Assert.Equal(GoogleLongAudioState.Pollable, (await session.AdvanceAsync(TestContext.Current.CancellationToken)).Checkpoint.State);
        Assert.NotNull((await session.PollAsync(TestContext.Current.CancellationToken)).Artifact);
        Assert.Equal(1, transport.Posts); Assert.NotNull(posted);
        using var wire = JsonDocument.Parse(posted);
        Assert.Equal(copy.Narration.Text, wire.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.DoesNotContain("84000.co", posted, StringComparison.Ordinal);
        Assert.Contains("84000.co", original.Text, StringComparison.Ordinal);
    }

    private static async Task<(NarrationBatch Batch, BatchItem Item, NarrationCopyPreparation Copy)> ImportBareBatchCopyAsync(Fixture f, ChirpEvidence e)
    {
        const string text = "Read 84000.co/translation?q=one%20two then continue.";
        using var queues = new FileBatchQueueStore(f.Paths.RootDirectory);
        var copies = new EfNarrationCopyStore(f, new(f.Paths), e.Clock);
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), f.Library, queues, narrationCopies: copies);
        var items = BatchPlanner.Plan([new BatchSourceFile("bare.txt", Hash(text), Encoding.UTF8.GetByteCount(text), text)], title => title + ".mp3", _ => false);
        var settings = new BatchSettings("cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "Synthetic", BatchEngines.LongAudioFirst,
            BatchFailurePolicy.SkipAndContinue, true, NarrationUrlOmission.CurrentVersion);
        var batch = await queues.SaveAsync(new(NarrationBatch.CurrentVersion, Guid.NewGuid(), "Bare synthetic", 0, BatchQueueState.Ready,
            settings, settings.Sha256(), f.Root, "USD", 2, items, e.Clock.GetUtcNow()), TestContext.Current.CancellationToken).ConfigureAwait(false);
        batch = await importer.ImportAsync(batch, new Dictionary<Guid, string> { [items[0].ItemId] = text }, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var item = Assert.Single(batch.Items);
        await BatchNarrationIdentity.ValidateAsync(f.Library, settings, item, copies, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var copy = Assert.IsType<NarrationCopyPreparation>(await copies.FindAsync(item.DocumentId!.Value, TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal("Read  then continue.", copy.Narration.Text);
        return (batch, item, copy);
    }
}
