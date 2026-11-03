using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public async Task NestedProviderExplanationsPersistRedactedWithExplicitCompletenessAndRestartReadback()
    {
        var ordinary = new EvidenceLogger<GoogleGenerationHttpTransport>();
        var privateEvents = new EvidenceLogger<GooglePrivateErrorDiagnostics>();
        using var f = new Fixture(logger: ordinary, privateLogger: privateEvents); f.Arm();
        f.Handler.Body = JsonSerializer.Serialize(new
        {
            error = new
            {
                status = "INVALID_ARGUMENT",
                message = "Root validation explanation.",
                details = new[] { new { reason = "SENTENCE_BOUNDARY_LIMIT", fieldViolations = new[] { new
                { field = "input.text", description = "Nested useful explanation. Bearer SYNTHETIC_SECRET_ACCESS_TOKEN; client_secret=SYNTHETIC_CLIENT_SECRET; SYNTHETIC PRIVATE INPUT DOCUMENT WITH A LONG SECRET FRAGMENT" } } } },
            },
        });
        var response = await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        var record = Assert.Single(new GooglePrivateErrorDiagnostics(f.Root, f.Clock).ReadRecords());
        Assert.Contains("Root validation explanation.", record.Message, StringComparison.Ordinal);
        Assert.Contains("Nested useful explanation.", record.Message, StringComparison.Ordinal);
        Assert.Contains("SENTENCE_BOUNDARY_LIMIT", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_SECRET_ACCESS_TOKEN", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_CLIENT_SECRET", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("INPUT DOCUMENT", record.Message, StringComparison.Ordinal);
        Assert.False(record.Truncated); Assert.False(record.ProviderDetailsOmitted);
        Assert.Equal("google-http-400", response.DiagnosticCode); Assert.Equal(1, f.Handler.Calls);
        string logs = string.Join('\n', ordinary.Messages.Concat(privateEvents.Messages));
        Assert.Contains("SavedPrivately", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("Root validation", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("Nested useful", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_SECRET_ACCESS_TOKEN", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedOnlyExplanationAndUnknownFieldsAreNotSilentlyLost()
    {
        using var f = new Fixture(); f.Arm();
        f.Handler.Body = "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"cause\":{\"message\":\"Nested-only diagnostic cause.\"},\"unrecognized\":\"PRIVATE_UNRECOGNIZED\"}]}}";
        await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        var record = Assert.Single(f.Store.ReadRecords());
        Assert.Equal("message: Nested-only diagnostic cause.", record.Message);
        Assert.True(record.ProviderDetailsOmitted);
        Assert.DoesNotContain("PRIVATE_UNRECOGNIZED", record.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("<html>PRIVATE_HTML</html>")]
    [InlineData("")]
    [InlineData("{\"error\":{\"status\":\"INVALID_ARGUMENT\"}}")]
    public async Task UnparseableOrMissingExplanationIsExplicitAndConsumesTicket(string body)
    {
        var logger = new EvidenceLogger<GoogleGenerationHttpTransport>();
        using var f = new Fixture(logger: logger); f.Arm(); f.Handler.Body = body;
        await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        Assert.Empty(f.Store.ReadRecords()); Assert.False(f.Store.IsArmed);
        Assert.Contains(logger.Messages, x => x.Contains(nameof(GoogleExplanationRetentionState.IneligibleOrUnparseable), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, x => x.Contains("PRIVATE_HTML", StringComparison.Ordinal));
        Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task StorageFailureHasSafeCorrelatedTerminalEventWithoutReplacingOutcome()
    {
        var logger = new EvidenceLogger<GooglePrivateErrorDiagnostics>();
        using var f = new Fixture(new BrokenWriter(), privateLogger: logger); f.Arm();
        var response = await f.SendAsync("Specific provider explanation, private only.");
        Assert.Equal("google-http-400", response.DiagnosticCode); Assert.Equal(1, f.Handler.Calls);
        Assert.Contains(logger.Messages, x => x.Contains(nameof(GoogleExplanationRetentionState.StorageFailed), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, x => x.Contains("Specific provider explanation", StringComparison.Ordinal));
        Assert.False(f.Store.IsArmed);
    }

    [Fact]
    public void LegacyPrivateRecordReportsUnknownNestedCompletenessWithoutRearming()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.Root);
        Guid id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(f.Root, id.ToString("N") + ".json"), JsonSerializer.Serialize(new
        {
            Id = id,
            CreatedUtc = f.Clock.Now,
            Message = "Legacy redacted explanation.",
            Truncated = false,
            f.Binding.PayloadSha256,
            TransportCorrelation = Guid.NewGuid().ToString("N"),
        }));
        var record = Assert.Single(f.Store.ReadRecords());
        Assert.Null(record.ProviderDetailsOmitted); Assert.False(f.Store.IsArmed);
    }

    [Fact]
    public async Task DetailOmissionSurvivesTruncationAndActualDiskReadback()
    {
        using var f = new Fixture(); f.Arm();
        f.Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = new string('x', 3_000), unrecognized = "PRIVATE_UNKNOWN" } });
        await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        var record = Assert.Single(new GooglePrivateErrorDiagnostics(f.Root, f.Clock).ReadRecords());
        Assert.True(record.Truncated); Assert.True(record.ProviderDetailsOmitted);
        Assert.DoesNotContain("PRIVATE_UNKNOWN", record.Message, StringComparison.Ordinal);
        Assert.All(Directory.GetFiles(f.Root), x => Assert.True(new FileInfo(x).Length <= 4096));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivatePersistenceLoggerFailureCannotLoseStoredExplanationOrCreateRetry(bool enabledThrows)
    {
        using var f = new Fixture(privateLogger: new EvidenceLogger<GooglePrivateErrorDiagnostics>(true, enabledThrows)); f.Arm();
        var response = await f.SendAsync("Useful explanation retained even when the observer fails.");
        Assert.Equal("google-http-400", response.DiagnosticCode); Assert.Equal(1, f.Handler.Calls);
        Assert.Equal("Useful explanation retained even when the observer fails.", Assert.Single(f.Store.ReadRecords()).Message);
    }

    private sealed class EvidenceLogger<T>(bool failLog = false, bool failIsEnabled = false) : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => failIsEnabled ? throw new InvalidOperationException("Synthetic observer failure") : true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (failLog) throw new InvalidOperationException("Synthetic observer failure");
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
