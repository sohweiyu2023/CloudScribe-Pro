using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task LongAudioSavedDocumentWorkflowPersistsReceiptAndSafeHistoryThenReopensWithoutAnyHttp()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var document = await f.Library.CreateAsync("Synthetic Long Audio", "\u4f60\u597d\u3002Synthetic source secret canary.", TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document);
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request);
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(history));
        var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new DocumentContentStore(f.Paths), fixture.Factory,
            e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history);
        var review = await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken);
        Assert.Null(await service.InspectOutputAsync(review, TestContext.Current.CancellationToken));
        var result = await service.RunAsync(review, fixture.Tools, true, new(), token: TestContext.Current.CancellationToken);
        Assert.Equal(LongAudioWorkflowStage.Completed, result.Stage); Assert.NotNull(result.Outcome.Artifact);
        Assert.Equal(4, fixture.Transport.Calls); Assert.Equal(1, fixture.Transport.Posts);
        var reopened = await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken);
        Assert.True(reopened.Cost.IsAlreadyCommitted); Assert.Equal(0, reopened.Cost.AdditionalCommitmentUsdCents);
        Assert.Equal(result.Outcome.Artifact, await service.InspectOutputAsync(reopened, TestContext.Current.CancellationToken));
        Assert.Equal(4, fixture.Transport.Calls);
        using var historyReader = new StreamReader(new FileStream(Path.Combine(f.Paths.RootDirectory, "data", "google-trend-history-v1.jsonl"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string text = historyReader.ReadToEnd();
        Assert.Contains("LongAudioWorkflow", text, StringComparison.Ordinal); Assert.Contains("GoogleInputStructure", text, StringComparison.Ordinal);
        Assert.DoesNotContain("source secret canary", text, StringComparison.Ordinal); Assert.DoesNotContain("synthetic-token", text, StringComparison.Ordinal);
        File.AppendAllText(result.Outcome.Artifact.LocalPath, "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectOutputAsync(reopened, TestContext.Current.CancellationToken));
        Assert.Equal(4, fixture.Transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongAudioChangedCurrentRevisionBlocksNewSpeechButCanResumeOriginalCommittedRevision(bool committed)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var document = await f.Library.CreateAsync("Synthetic revision test", "Original synthetic source.", TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document);
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request);
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(history));
        var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new DocumentContentStore(f.Paths), fixture.Factory,
            e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history);
        if (committed)
        {
            using var session = await fixture.Factory.OpenAsync(p.Request, fixture.Tools, new(4096, null, "mp3", 24000, 1), true, TestContext.Current.CancellationToken);
            Assert.Equal(GoogleLongAudioState.Pollable, (await session.AdvanceAsync(TestContext.Current.CancellationToken)).Checkpoint.State);
        }
        _ = await f.Library.SaveAsync(new(document.Id, document.Title, "Edited synthetic source.", document.ConcurrencyVersion,
            DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken);
        if (!committed) await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(document.Id, TestContext.Current.CancellationToken));
        else
        {
            var resumed = await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken);
            Assert.True(resumed.HistoricalRevision); Assert.Equal(document.CurrentRevisionId, resumed.RevisionId);
            Assert.Equal(p.Request.BindingSha256, resumed.Request.BindingSha256);
            Assert.Equal(GoogleLongAudioContinuationKind.ResumeExistingOperation,
                (await service.InspectContinuationAsync(resumed, TestContext.Current.CancellationToken)).Kind);
        }
        Assert.Equal(committed ? 1 : 0, fixture.Transport.Posts);
    }

    [Fact]
    public async Task LongAudioStoppedBeforeSubmissionKeepsReviewCorrectableAndDoesNotConsumeTheAttempt()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var document = await f.Library.CreateAsync("Synthetic stop", "Synthetic source.", TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document);
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request);
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(history));
        var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new DocumentContentStore(f.Paths), fixture.Factory,
            e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history);
        var review = await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken);
        byte[] before = File.ReadAllBytes(p.LedgerPath); var control = new GoogleLongAudioRunControl(); control.RequestStop();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunAsync(review, fixture.Tools, true, control, token: TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Transport.Calls); Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
        Assert.Equal(GoogleLongAudioContinuationKind.CorrectLocalSetup, (await service.InspectContinuationAsync(review, TestContext.Current.CancellationToken)).Kind);
    }
}
