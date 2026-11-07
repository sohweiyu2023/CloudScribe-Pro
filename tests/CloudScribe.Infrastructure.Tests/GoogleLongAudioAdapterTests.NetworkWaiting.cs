using System.Net;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public async Task SlowPostAndPollDoNotManufactureUnknownOutcomesFromLocalDeadlines()
    {
        using var f = new Fixture(); var request = Request(); var gate = new Admission(); int posts = 0, polls = 0;
        using var adapter = f.Adapter(gate, async (message, token) =>
        {
            bool post = message.Method == HttpMethod.Post;
            if (post) posts++; else polls++;
            await Task.Delay(TimeSpan.FromSeconds(26), token).ConfigureAwait(false); // Cross both former 12s and25s deadlines.
            return Json(Operation(!post));
        });
        var submitted = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.Pollable, submitted.State);
        Assert.True(submitted.Observation!.ReadbackVerified); Assert.False(submitted.Observation.Truncated);
        var completed = await adapter.PollOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.CompletedUnverified, completed.State);
        Assert.True(completed.Observation!.ReadbackVerified); Assert.False(completed.Observation.Truncated);
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, posts); Assert.Equal(1, polls); Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task SlowObjectReadRemainsVerifiedWithoutAnArtificialHttpDeadline()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request(); int gets = 0;
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        using var boundary = output.Boundary(f.Journal, async (_, token) =>
        {
            gets++;
            if (gets == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(13), token).ConfigureAwait(false);
                return Json(OutputMetadata(request, output.Audio));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.Audio) };
        });
        var result = await boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(result.LocalPath)); Assert.Equal(2, gets);
        Assert.Equal(result, await boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(2, gets);
    }

    [Fact]
    public async Task CallerCancellationStillPreservesTheUnknownPostAndForbidsResend()
    {
        using var f = new Fixture(); var request = Request(); var gate = new Admission(); int calls = 0;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var adapter = f.Adapter(gate, async (_, token) =>
        {
            calls++; entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable synthetic transport.");
        });
        var pending = adapter.SubmitOnceAsync(request, cancel.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(GoogleLongAudioState.PostClaimed, f.Journal.Load(request.AttemptId).State);
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(1, gate.Calls);
    }
}
