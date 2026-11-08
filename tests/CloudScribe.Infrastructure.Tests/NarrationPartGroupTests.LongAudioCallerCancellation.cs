using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task LongAudioCallerCancellationPreservesTheActualLedgerAndForbidsAnotherPost()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongInstalledAsync(f, e);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int posts = 0;
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request,
            () => new WaitingCancellationHandler(entered, () => posts++));
        using var session = await fixture.Factory.OpenAsync(p.Request, fixture.Tools,
            new(4096, null, "mp3", 24000, 1), true, TestContext.Current.CancellationToken);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = session.AdvanceAsync(cancel.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        byte[] held = File.ReadAllBytes(p.LedgerPath);
        var ledger = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(held)!;
        Assert.Single(ledger.Claims); Assert.Equal(1, ledger.Claims[0].MaximumUsdCents);
        Assert.Equal(p.Request.AttemptId, ledger.Claims[0].AttemptId);
        var journal = new GoogleLongAudioFileJournal(Path.Combine(p.Root, "operations"));
        Assert.Equal(GoogleLongAudioState.PostClaimed, journal.Load(p.Request.AttemptId).State);
        _ = await Record.ExceptionAsync(() => session.AdvanceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, posts); Assert.Equal(held, File.ReadAllBytes(p.LedgerPath));
    }

    private sealed class WaitingCancellationHandler(TaskCompletionSource entered, Action recordPost) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method); recordPost(); entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable synthetic HTTP response.");
        }
    }
}
