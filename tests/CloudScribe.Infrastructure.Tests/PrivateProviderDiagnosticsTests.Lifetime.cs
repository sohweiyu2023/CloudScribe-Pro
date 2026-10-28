using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public async Task WallClockRollbackCannotExtendDiagnosticOptIn()
    {
        using var f = new Fixture(); f.Arm();
        f.Clock.Now -= TimeSpan.FromHours(1); f.Clock.Timestamp += TimeSpan.FromMinutes(11).Ticks;
        await f.SendAsync("Expired diagnostic must not be retained"); Assert.Empty(f.Store.ReadRecords());
    }

    [Fact]
    public async Task ResponsePastMonotonicExpiryDoesNotRetainText()
    {
        using var f = new Fixture(); f.Arm();
        f.Handler.BeforeResponse = () => { f.Clock.Now -= TimeSpan.FromHours(1); f.Clock.Timestamp += TimeSpan.FromMinutes(11).Ticks; };
        var result = await f.SendAsync("Late response must not be retained");
        Assert.Equal("google-http-400", result.DiagnosticCode); Assert.Empty(f.Store.ReadRecords());
    }

    [Fact]
    public async Task ConcurrentExplicitFakeCallsCannotReuseOneDiagnosticTicket()
    {
        using var f = new Fixture(); f.Arm();
        f.Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = "One private response only." } });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken)));
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        Assert.Single(f.Store.ReadRecords()); Assert.Equal(8, f.Handler.Calls); // Eight intentional fake calls; capture itself made none.
    }

    [Fact]
    public async Task TransportFailureConsumesOptInWithoutRearmingForLaterRequest()
    {
        using var f = new Fixture(); f.Arm(); f.Handler.Failure = new HttpRequestException("Synthetic local transport failure");
        var failed = await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        Assert.Equal("google-transport-unknown", failed.DiagnosticCode);
        f.Handler.Failure = null; await f.SendAsync("Later response must not be captured"); Assert.Empty(f.Store.ReadRecords());
    }

    [Fact]
    public async Task UnexpectedPrivateDirectoryFileIsPreservedAndFailsClosed()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.Root);
        string other = Path.Combine(f.Root, "unrecognized-fixture.txt"); File.WriteAllText(other, "Synthetic unrelated file");
        f.Arm(); var result = await f.SendAsync("A useful error that cannot safely be stored here.");
        Assert.Equal("google-http-400", result.DiagnosticCode);
        Assert.Equal("Synthetic unrelated file", File.ReadAllText(other)); Assert.Empty(f.Store.ReadRecords());
        Assert.Contains("could not be saved", f.Store.PersistenceStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrderlyDisposeIsBoundedEvenWhenPrivateWriterIsBlocked()
    {
        using var writer = new BlockingWriter(); using var f = new Fixture(writer); f.Arm();
        f.Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = "Synthetic shutdown detail." } });
        try
        {
            await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
            Assert.True(writer.Started.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            await Task.Run(f.Store.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(f.Store.IsArmed); Assert.Throws<ObjectDisposedException>(() => f.Store.Arm(f.Binding, true));
        }
        finally { writer.Release.Set(); await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public void NormalizedAndEscapedInputFragmentsAreSuppressed()
    {
        const string original = "PRIVATE Cafe\u0301 DOCUMENT\r\nSYNTHETIC SECRET FRAGMENT";
        byte[] input = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = original } });
        foreach (string echo in new[] { original.Normalize(NormalizationForm.FormC).ReplaceLineEndings("\n"),
            JsonSerializer.Serialize(original)[1..^1], Uri.EscapeDataString(original), "SYNTHETIC SECRET FRAGMENT" })
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { error = new { status = "INVALID_ARGUMENT", message = "Novel validation: " + echo } });
            string redacted = PrivateProviderMessageRedactor.Extract(body, input, "synthetic-token")!;
            Assert.DoesNotContain(echo, redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC SECRET", redacted, StringComparison.Ordinal);
            Assert.Contains("Novel validation", redacted, StringComparison.Ordinal);
        }
    }
}
