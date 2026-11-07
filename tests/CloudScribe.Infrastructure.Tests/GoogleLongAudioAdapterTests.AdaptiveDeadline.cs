using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealProtectedDeadlineEvidenceContinuesOnlyGetAndStopsIfRetentionFails(bool storageFailure)
    {
        using var fixture = new Fixture();
        Guid id = Guid.NewGuid();
        var request = GoogleLongAudioRequest.Create(id, "123456", "global", "synthetic-ref",
            "gs://synthetic-private/output/" + id.ToString("N") + ".wav", "synthetic complete paragraph.",
            "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "LINEAR16");
        int posts = 0, polls = 0, saves = 0;
        var store = new ProtectedStore
        {
            BeforeSave = () =>
        {
            if (++saves == 2 && storageFailure) throw new IOException("Synthetic evidence write failure.");
        }
        };
        var observer = new GoogleLongAudioProtectedResponseObserver(store, TimeProvider.System);
        var admission = new Admission();
        using var adapter = new GoogleLongAudioAdapter(new Credentials(), admission, fixture.Journal, observer,
            new Handler(async (message, token) =>
            {
                if (message.Method == HttpMethod.Post) { posts++; return Json(Operation(false)); }
                if (++polls == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return Json(Operation(true));
            }))
        { PollDeadline = TimeSpan.FromMilliseconds(100) };
        bool sawVerifiedDeadline = false;
        var runner = new GoogleLongAudioRecoveryRunner(async (current, poll, token) =>
        {
            observer.ArmForJob(current, true);
            var state = poll ? await adapter.PollOnceAsync(current, token).ConfigureAwait(false)
                : await adapter.SubmitOnceAsync(current, token).ConfigureAwait(false);
            if (polls == 1)
            {
                Assert.Equal(GoogleResponseReadState.Cancelled, state.Observation!.ReadState);
                Assert.True(state.Observation.Truncated);
                Assert.Equal(!storageFailure, state.HasVerifiedPollDeadline);
                Assert.Equal(state, fixture.Journal.Load(current.AttemptId));
                sawVerifiedDeadline = state.HasVerifiedPollDeadline;
            }
            GoogleLongAudioVerifiedArtifact? artifact = state.State == GoogleLongAudioState.CompletedUnverified
                ? new(new(current.BindingSha256, "synthetic-private", "output/" + id.ToString("N") + ".wav",
                    "1", 12, "AAAAAAAAAAAAAAAAAAAAAA=="), "synthetic.wav", new string('a', 64), 12) : null;
            return new(state, artifact);
        }, (_, _) => throw new InvalidOperationException("No split or new paid request is expected."),
            30, TimeProvider.System);
        var result = await runner.RunAsync(request, new GoogleLongAudioRunControl(), token: TestContext.Current.CancellationToken);
        Assert.Equal(!storageFailure, result.CoverageComplete);
        Assert.Equal(!storageFailure, sawVerifiedDeadline);
        Assert.Equal(1, posts);
        Assert.Equal(storageFailure ? 1 : 2, polls);
        Assert.Equal(1, admission.Calls);
        Assert.Equal(storageFailure ? 0 : 1, result.Outputs.Count);
        Assert.False(fixture.Journal.Load(request.AttemptId).HasVerifiedPollDeadline);
        if (!storageFailure) Assert.True(store.Records[1].Response!.Truncated);
    }
}
