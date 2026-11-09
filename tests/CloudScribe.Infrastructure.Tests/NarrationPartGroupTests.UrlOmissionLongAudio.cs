using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("https://example.org/private")]
    [InlineData("84000.co/translation?q=one%20two")]
    public async Task NarrationCopyLongAudioDispatchUsesActualSavedCopyTextAndPreservesOriginal(string address)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var original = await f.Library.CreateAsync("Original links", "Read " + address + " then continue.", TestContext.Current.CancellationToken);
        var copy = await new EfNarrationCopyStore(f, new DocumentContentStore(f.Paths), e.Clock).PrepareAsync(original.Id,
            original.CurrentRevisionId!.Value, Hash(original.Text), TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: copy.Narration);
        var transport = new LongRuntimeTransport(p.Request);
        string? posted = null;
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request, () => new CopyLongAudioHandler(transport, text => posted = text));
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(history));
        var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new DocumentContentStore(f.Paths), fixture.Factory,
            e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history);
        var review = await service.ReviewAsync(copy.Narration.Id, TestContext.Current.CancellationToken);
        var result = await service.RunAsync(review, fixture.Tools, true, new(), token: TestContext.Current.CancellationToken);
        Assert.Equal(LongAudioWorkflowStage.Completed, result.Stage);
        Assert.Equal(1, transport.Posts); Assert.NotNull(posted);
        using var wire = JsonDocument.Parse(posted);
        Assert.Equal(copy.Narration.Text, wire.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.DoesNotContain("https://", posted, StringComparison.Ordinal);
        Assert.DoesNotContain("84000.co", posted, StringComparison.Ordinal);
        Assert.Equal(NarrationUrlOmission.CurrentVersion, copy.TransformVersion);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(copy.Narration.Text), review.Cost.InputUtf8Bytes);
        Assert.Equal(review.Request.BindingSha256, review.Cost.RequestBindingSha256);
        Assert.Equal(copy.Narration.Id, review.DocumentId); Assert.Equal(copy.Narration.CurrentRevisionId, review.RevisionId);
        Assert.Equal(Hash(copy.Narration.Text), review.SourceSha256);
        Assert.Equal(original.Text, (await f.Library.OpenAsync(original.Id, TestContext.Current.CancellationToken))!.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NarrationCopyLongAudioSourceEditBlocksNewDispatchButKeepsCommittedExactOperation(bool committed)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var original = await f.Library.CreateAsync("Original links", "Read https://example.org then continue.", TestContext.Current.CancellationToken);
        var copy = await new EfNarrationCopyStore(f, new DocumentContentStore(f.Paths), e.Clock).PrepareAsync(original.Id,
            original.CurrentRevisionId!.Value, Hash(original.Text), TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: copy.Narration);
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request);
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(history));
        var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new DocumentContentStore(f.Paths), fixture.Factory,
            e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history);
        var review = await service.ReviewAsync(copy.Narration.Id, TestContext.Current.CancellationToken);
        byte[] ledgerBefore = File.ReadAllBytes(p.LedgerPath);
        using var session = await service.OpenAsync(review, fixture.Tools, true, TestContext.Current.CancellationToken);
        if (committed)
            Assert.Equal(GoogleLongAudioState.Pollable, (await session.AdvanceAsync(TestContext.Current.CancellationToken)).Checkpoint.State);
        await f.Library.SaveAsync(new(original.Id, original.Title, "Changed original.", original.ConcurrencyVersion,
            DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken);
        if (!committed)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.AdvanceAsync(TestContext.Current.CancellationToken));
            Assert.Equal(ledgerBefore, File.ReadAllBytes(p.LedgerPath));
            var installed = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken);
            Assert.False(File.Exists(Path.Combine(installed!.OperationsDirectory, review.Request.AttemptId.ToString("N") + ".json")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(copy.Narration.Id, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(review, fixture.Tools, true, new(), token: TestContext.Current.CancellationToken));
        }
        else
        {
            var retained = await service.ReviewAsync(copy.Narration.Id, TestContext.Current.CancellationToken);
            Assert.True(retained.Cost.IsAlreadyCommitted); Assert.Equal(review.Request.BindingSha256, retained.Request.BindingSha256);
            var completed = await service.RunAsync(retained, fixture.Tools, true, new(), token: TestContext.Current.CancellationToken);
            Assert.Equal(LongAudioWorkflowStage.Completed, completed.Stage);
        }
        Assert.Equal(committed ? 1 : 0, fixture.Transport.Posts);
    }

    private sealed class CopyLongAudioHandler(LongRuntimeTransport transport, Action<string> observe) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post) observe(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return transport.Send(request);
        }
    }
}
