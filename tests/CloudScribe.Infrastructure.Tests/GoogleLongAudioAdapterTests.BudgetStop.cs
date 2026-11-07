using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public async Task FinalAdmissionRacePreservesClaimAndNeverSendsOrAutomaticallyRetries()
    {
        using var fixture = new Fixture(); var request = Request(); int sent = 0;
        var gate = new PreflightRaceAdmission();
        using var adapter = new GoogleLongAudioAdapter(new Credentials(), gate, fixture.Journal,
            new ReadyBudgetObserver(), new Handler((_, _) => { sent++; return Task.FromResult(Json(Operation(false))); }));
        await Assert.ThrowsAsync<GoogleLongAudioBudgetExceededException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, gate.Preflights); Assert.Equal(1, gate.Admissions); Assert.Equal(0, sent);
        var saved = fixture.Journal.Load(request.AttemptId);
        Assert.Equal(GoogleLongAudioState.PostClaimed, saved.State); Assert.Null(saved.OperationName); Assert.Null(saved.Observation);
        Assert.Equal(saved, await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, gate.Preflights); Assert.Equal(1, gate.Admissions); Assert.Equal(0, sent);
    }

    private sealed class PreflightRaceAdmission : IGoogleLongAudioAdmission, IGoogleLongAudioSubmissionPreflight
    {
        public int Preflights { get; private set; }
        public int Admissions { get; private set; }
        public ValueTask RequireBeforeJournalAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Preflights++; return ValueTask.CompletedTask; }
        public ValueTask RequireSubmissionAuthorizedAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Admissions++; throw new GoogleLongAudioBudgetExceededException(2001, 2000); }
    }

    internal sealed class ReadyBudgetObserver : IGoogleLongAudioResponseObserver
    {
        public bool IsReadyFor(GoogleLongAudioRequest request) => true;
        public Guid Begin(GoogleLongAudioRequest request) => Guid.NewGuid();
        public bool IsActiveFor(Guid ticket, GoogleLongAudioRequest request) => true;
        public void FinishResponse(Guid ticket) { }
        public Task<GoogleLongAudioObservation> ObserveAsync(Guid ticket, GoogleLongAudioRequest request, int httpStatus,
            ReadOnlyMemory<byte> boundedBody, GoogleResponseReadState readState, string transientAccessToken, bool operationError) =>
            throw new InvalidOperationException("No provider response should exist in the admission race fixture.");
    }
}
