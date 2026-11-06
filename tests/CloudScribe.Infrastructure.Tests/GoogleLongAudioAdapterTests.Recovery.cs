using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public async Task ConfirmedSentenceKindSurvivesJournalRestartWithoutPersistingTheMessageOrRepeatingPost()
    {
        using var fixture = new Fixture(); var request = Request(); int posts = 0;
        const string message = "This request contains sentences that are too long. Consider splitting up long sentences with sentence ending punctuation e.g. periods. PRIVATE-CANARY";
        using (var adapter = fixture.Adapter(new Admission(), (_, _) =>
        { posts++; return Task.FromResult(Json(JsonSerializer.Serialize(new { name = Name, done = true, error = new { code = 3, message } }))); }))
        {
            var result = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(GoogleLongAudioFailureKind.SentenceLengthReported, result.FailureKind);
        }
        Assert.Equal(GoogleLongAudioFailureKind.SentenceLengthReported, fixture.Journal.Load(request.AttemptId).FailureKind);
        using var restart = fixture.Adapter(new Admission(), (_, _) => throw new InvalidOperationException("No repeated POST."));
        _ = await restart.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, posts); Assert.DoesNotContain("PRIVATE-CANARY", fixture.JournalText(), StringComparison.Ordinal);
    }
}
