using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedLongAudioBudgetRejectsReviewAndAdapterBeforeJournalOrCredentials(bool priorHold)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var evidence = await ChirpEvidenceAsync(f);
        int readiness = 0;
        long cap = priorHold ? 915 : 914;
        var p = await LongLedgerFixtureAsync(f, evidence.Admission.Policy!, evidence.Clock, cap: cap,
            readiness: (_, _, _) => { readiness++; return ValueTask.CompletedTask; });
        if (priorHold)
        {
            var book = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
            File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(book with
            { Claims = [new(Guid.NewGuid(), new string('e', 64), 1)] }));
        }
        byte[] ledger = File.ReadAllBytes(p.LedgerPath);
        var error = await Assert.ThrowsAsync<GoogleLongAudioBudgetExceededException>(() => p.Admission.ReviewAsync(p.Request, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(cap + 1, error.ProvisionUsdCents); Assert.Equal(cap, error.CapUsdCents);
        Assert.Contains(priorHold ? "US$9.16" : "US$9.15", error.Message, StringComparison.Ordinal);
        Assert.Contains("No new synthesis request was sent", error.Message, StringComparison.Ordinal);
        string operations = Path.Combine(f.Root, "budget-stop-operations");
        using var adapter = new GoogleLongAudioAdapter(new BudgetStopCredentials(), p.Admission,
            new GoogleLongAudioFileJournal(operations), new GoogleLongAudioAdapterTests.ReadyBudgetObserver(), new BudgetStopHandler());
        for (int n = 0; n < 2; n++)
            await Assert.ThrowsAsync<GoogleLongAudioBudgetExceededException>(() => adapter.SubmitOnceAsync(p.Request, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(operations, "*.json"));
        Assert.False(File.Exists(Path.Combine(operations, "observation-pending.guard")));
        Assert.Equal(0, readiness); Assert.Equal(ledger, File.ReadAllBytes(p.LedgerPath));
    }

    private sealed class BudgetStopCredentials : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Credentials must not be read for an exhausted local budget.");
    }
    private sealed class BudgetStopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network call belongs to a rejected local funding review.");
    }
}
