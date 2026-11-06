using System.Net;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public async Task SlowDurableCaptureDoesNotBecomeAnArtificialStorageFailureOrAllowAnotherPost()
    {
        using var fixture = new Fixture(); using var release = new ManualResetEventSlim(); var request = Request();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = TestContext.Current.CancellationToken;
        var store = new ProtectedStore { BeforeSave = () => { started.SetResult(); release.Wait(token); } };
        var observer = new GoogleLongAudioProtectedResponseObserver(store, new Clock()); observer.ArmForJob(request, true);
        int calls = 0;
        using var adapter = fixture.Adapter(new Admission(), (_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"Synthetic retained explanation.\"}}") });
        }, observer);
        var pending = adapter.SubmitOnceAsync(request, token);
        try
        {
            await started.Task.WaitAsync(token);
            await Task.Delay(TimeSpan.FromSeconds(3.2), token);
            Assert.False(pending.IsCompleted);
            var durable = fixture.Journal.Load(request.AttemptId);
            Assert.Equal(400, durable.HttpStatus); Assert.Equal(GoogleLongAudioState.Failed, durable.State);
            Assert.Null(durable.Observation);
            Assert.Throws<InvalidOperationException>(() => observer.ArmForJob(Request(), true));
            Assert.Equal(1, calls);
        }
        finally { release.Set(); }
        var result = await pending;
        Assert.True(result.Observation!.ReadbackVerified); Assert.Single(store.Records); Assert.Equal(1, calls);
    }
}
