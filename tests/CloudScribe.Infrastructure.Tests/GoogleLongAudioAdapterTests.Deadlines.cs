using System.Net.Sockets;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public async Task UnansweredStatusCheckStaysPollableIsLoggedAndNeverResubmits()
    {
        using var f = new Fixture(); var request = Request(); var gate = new Admission(); var log = new ExchangeLog();
        int posts = 0, polls = 0;
        using var adapter = new GoogleLongAudioAdapter(new Credentials(), gate, f.Journal, new ReadyObserver(),
            new Handler(async (message, token) =>
            {
                if (message.Method == HttpMethod.Post) { posts++; return Json(Operation(false)); }
                if (++polls == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return Json(Operation(true));
            }), log)
        { PollDeadline = TimeSpan.FromMilliseconds(250) };

        Assert.Equal(GoogleLongAudioState.Pollable, (await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken)).State);
        var stalled = await adapter.PollOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.Pollable, stalled.State);
        Assert.Contains(log.Messages, x => x.Contains("received no answer within", StringComparison.Ordinal)
            && x.Contains(request.AttemptId.ToString(), StringComparison.Ordinal));

        var completed = await adapter.PollOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.CompletedUnverified, completed.State);
        Assert.Equal(1, posts); Assert.Equal(2, polls); Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task CallerCancellationOfAStatusCheckIsNotMistakenForTheDeadline()
    {
        using var f = new Fixture(); var request = Request();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var adapter = new GoogleLongAudioAdapter(new Credentials(), new Admission(), f.Journal, new ReadyObserver(),
            new Handler(async (message, token) =>
            {
                if (message.Method == HttpMethod.Post) return Json(Operation(false));
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable synthetic transport.");
            }))
        { PollDeadline = TimeSpan.FromHours(1) };
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        var pending = adapter.PollOnceAsync(request, cancel.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(GoogleLongAudioState.Pollable, f.Journal.Load(request.AttemptId).State);
    }

    [Fact]
    public void ProductionTransportDetectsDeadConnectionsWithoutAPostDeadline()
    {
        using var handler = GoogleLongAudioTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
        Assert.InRange(GoogleLongAudioTransport.KeepAliveIdle.TotalSeconds + GoogleLongAudioTransport.KeepAliveInterval.TotalSeconds * GoogleLongAudioTransport.KeepAliveProbes, 60, 300);
        Assert.True(GoogleLongAudioTransport.PollResponseDeadline > TimeSpan.Zero);
        Assert.True(GoogleLongAudioTransport.StorageReadDeadline >= TimeSpan.FromMinutes(10));
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)GoogleLongAudioTransport.KeepAliveIdle.TotalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, GoogleLongAudioTransport.KeepAliveProbes);
    }
}
