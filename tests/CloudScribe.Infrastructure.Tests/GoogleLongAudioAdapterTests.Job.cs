using System.Net;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public async Task JobRejectsMixedOperationJournalsBeforeStorageAccess()
    {
        using var first = new Fixture(); using var other = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = first.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        using var otherAdapter = other.Adapter(new Admission(), (_, _) =>
            Task.FromResult(Json(Operation(true).Replace(Name, Name + "-other", StringComparison.Ordinal))));
        _ = await otherAdapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        using var boundary = output.Boundary(other.Journal, (_, _) => throw new InvalidOperationException("No mixed-journal GET allowed."));
        var job = new GoogleLongAudioJob(adapter, boundary);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => job.AdvanceAsync(request, TestContext.Current.CancellationToken));
        Assert.Contains("Output journal differs", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Native.Calls);
    }

    [Fact]
    public async Task JobSeparatesSubmissionCaptureFromExplicitPolling()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        int posts = 0, polls = 0, reads = 0;
        using var adapter = f.Adapter(new Admission(), (message, _) =>
        {
            if (message.Method == HttpMethod.Post) { posts++; return Task.FromResult(Json(Operation(false))); }
            polls++; return Task.FromResult(Json(Operation(true)));
        });
        using var boundary = output.Boundary(f.Journal, (_, _) =>
        {
            reads++;
            return Task.FromResult(reads == 1 ? Json(OutputMetadata(request, output.Audio))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.Audio) });
        });
        var job = new GoogleLongAudioJob(adapter, boundary);
        var submitted = await job.AdvanceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.Pollable, submitted.Checkpoint.State);
        Assert.Null(submitted.Artifact); Assert.Equal(0, polls); Assert.Equal(0, reads);
        var completed = await job.PollAsync(request, TestContext.Current.CancellationToken);
        Assert.NotNull(completed.Artifact); Assert.Equal(1, posts); Assert.Equal(1, polls); Assert.Equal(2, reads);
    }

    [Fact]
    public async Task JobResumesCompletedOperationWithoutAnotherPostOrStorageRead()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        int posts = 0, gets = 0;
        using var adapter = f.Adapter(new Admission(), (_, _) =>
        { posts++; return Task.FromResult(Json(Operation(true))); });
        using var boundary = output.Boundary(f.Journal, (_, _) =>
        {
            gets++;
            return Task.FromResult(gets == 1 ? Json(OutputMetadata(request, output.Audio))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.Audio) });
        });
        var job = new GoogleLongAudioJob(adapter, boundary);
        var first = await job.AdvanceAsync(request, TestContext.Current.CancellationToken);
        var resumed = await job.AdvanceAsync(request, TestContext.Current.CancellationToken);
        Assert.NotNull(first.Artifact); Assert.Equal(first.Artifact, resumed.Artifact);
        Assert.Equal(1, posts); Assert.Equal(2, gets);
    }

    [Fact]
    public async Task JobDoesNotReplayUnknownPostOrFetchAnUnfinishedOutput()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        int posts = 0;
        using var adapter = f.Adapter(new Admission(), (_, _) =>
        { posts++; throw new HttpRequestException("synthetic transport interruption"); });
        using var boundary = output.Boundary(f.Journal, (_, _) => throw new InvalidOperationException("No object read allowed."));
        var job = new GoogleLongAudioJob(adapter, boundary);
        await Assert.ThrowsAsync<HttpRequestException>(() => job.AdvanceAsync(request, TestContext.Current.CancellationToken));
        var resumed = await job.AdvanceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.PostClaimed, resumed.Checkpoint.State);
        Assert.Null(resumed.Artifact); Assert.Equal(1, posts); Assert.Equal(0, output.Native.Calls);
    }
}
