using System.Text;
using System.Text.Json;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchLoggingTests
{
    [Fact]
    public void CompleteLifecycleAndJobIdentitySurviveRestartAndRoutineLogRemoval()
    {
        using var fixture = new LoggingFixture();
        Guid batch = Guid.NewGuid(), item = Guid.NewGuid(), job = Guid.NewGuid();
        string logPath;
        using (var ordinary = new BoundedJsonFileLoggerProvider(fixture.Paths, fixture.Options, TimeProvider.System))
        using (var history = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(ordinary).AddProvider(history)))
        {
            logPath = ordinary.CurrentLogPath;
            var logger = factory.CreateLogger("SYNTHETIC_PRIVATE_CATEGORY");
            CloudScribeLog.BatchCreated(logger, batch, 2, 1);
            CloudScribeLog.BatchRunStarted(logger, batch, 2, 0);
            CloudScribeLog.BatchItemStarted(logger, batch, 1, false);
            CloudScribeLog.BatchItemJobLinked(logger, batch, item, 1, job);
            CloudScribeLog.BatchItemCompleted(logger, batch, 1, 123, 456);
            CloudScribeLog.BatchItemStopped(logger, batch, 2, BatchItemState.Failed, "batch.output-not-verified");
            CloudScribeLog.BatchRunSummary(logger, batch, BatchQueueState.Finished, 1, 1, 0, 2, 234, 500, 400, "batch.finished");
            history.RequireReady();
        }
        string ordinaryText = File.ReadAllText(logPath);
        var ordinaryRows = Rows(ordinaryText);
        AssertLifecycle(ordinaryRows, batch, item, job);
        AssertLifecycle(Rows(Export(ordinaryText)), batch, item, job);
        File.Delete(logPath);
        using (var reopened = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        {
            reopened.RequireReady();
            string retained = ReadHistory(fixture.History);
            AssertLifecycle(Rows(retained).Where(x => string.Equals(x.GetProperty("Kind").GetString(), "Event", StringComparison.Ordinal)).ToArray(), batch, item, job);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_CATEGORY", ordinaryText + retained, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("batch.narration-failed-before-start")]
    [InlineData("batch.narration-failed-after-start-reconcile")]
    [InlineData("batch.output-not-verified")]
    [InlineData("batch.output-missing-or-changed")]
    [InlineData("batch.long-audio-needs-reconciliation")]
    [InlineData("batch.local-copy-failed-speech-kept")]
    [InlineData("batch.plan-changed-nothing-sent")]
    [InlineData("batch.file-over-approved-maximum-nothing-sent")]
    [InlineData("batch.not-authorized")]
    [InlineData("batch.paused")]
    [InlineData("batch.interrupted")]
    [InlineData("batch.finished")]
    [InlineData("article.provider-rejected-no-retry")]
    [InlineData("article.provider-outcome-uncertain-no-retry")]
    [InlineData("article.stopped-reconcile")]
    public void FixedReasonsRemainCompleteInBothSinksAndSafeExport(string reason)
    {
        using var fixture = new LoggingFixture();
        string path;
        using (var ordinary = new BoundedJsonFileLoggerProvider(fixture.Paths, fixture.Options, TimeProvider.System))
        using (var history = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(ordinary).AddProvider(history)))
        {
            path = ordinary.CurrentLogPath;
            CloudScribeLog.BatchItemStopped(factory.CreateLogger("ignored"), Guid.NewGuid(), 1, BatchItemState.Uncertain, reason);
        }
        string text = File.ReadAllText(path);
        foreach (var row in Rows(text).Concat(Rows(Export(text))).Concat(Rows(ReadHistory(fixture.History)).Where(x => string.Equals(x.GetProperty("Kind").GetString(), "Event", StringComparison.Ordinal))))
        {
            Assert.True(row.GetProperty("FieldsComplete").GetBoolean());
            Assert.Equal(reason, row.GetProperty("SafeFields").GetProperty("ReasonCode").GetString());
        }
    }

    [Theory]
    [InlineData("batch.synthetic-private-canary")]
    [InlineData("article.synthetic-private-canary")]
    [InlineData("C:\\Users\\synthetic-private-canary\\article.txt")]
    [InlineData("unpublished synthetic-private-canary text")]
    public void UnknownReasonTextIsNeverRetainedOrExported(string reason)
    {
        using var fixture = new LoggingFixture();
        string path;
        using (var ordinary = new BoundedJsonFileLoggerProvider(fixture.Paths, fixture.Options, TimeProvider.System))
        using (var history = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(ordinary).AddProvider(history)))
        {
            path = ordinary.CurrentLogPath;
            var logger = factory.CreateLogger(reason);
            CloudScribeLog.BatchItemStopped(logger, Guid.NewGuid(), 1, BatchItemState.Uncertain, reason);
            CloudScribeLog.BatchRunSummary(logger, Guid.NewGuid(), BatchQueueState.Paused, 0, 0, 1, 1, 0, 1, 1, reason);
        }
        string ordinaryText = File.ReadAllText(path), historyText = ReadHistory(fixture.History), exported = Export(ordinaryText);
        Assert.DoesNotContain("synthetic-private-canary", ordinaryText + historyText + exported, StringComparison.Ordinal);
        foreach (var row in Rows(ordinaryText).Concat(Rows(exported)).Concat(Rows(historyText).Where(x => string.Equals(x.GetProperty("Kind").GetString(), "Event", StringComparison.Ordinal))))
        {
            Assert.False(row.GetProperty("FieldsComplete").GetBoolean());
            Assert.False(row.GetProperty("SafeFields").TryGetProperty("ReasonCode", out _));
        }
    }

    private static void AssertLifecycle(JsonElement[] rows, Guid batch, Guid item, Guid job)
    {
        Assert.Equal([1640, 1641, 1642, 1646, 1643, 1644, 1645], rows.Select(x => x.GetProperty("EventId").GetInt32()).ToArray());
        Assert.All(rows, row => Assert.True(row.GetProperty("FieldsComplete").GetBoolean()));
        Assert.All(rows, row => Assert.Equal(batch, row.GetProperty("SafeFields").GetProperty("BatchId").GetGuid()));
        var created = rows[0].GetProperty("SafeFields");
        Assert.Equal("2", created.GetProperty("Included").GetString());
        Assert.Equal("1", created.GetProperty("Excluded").GetString());
        var link = rows[3].GetProperty("SafeFields");
        Assert.Equal(item, link.GetProperty("ItemId").GetGuid());
        Assert.Equal(job, link.GetProperty("JobId").GetGuid());
        Assert.Equal("1", link.GetProperty("Order").GetString());
        Assert.Equal("batch.output-not-verified", rows[5].GetProperty("SafeFields").GetProperty("ReasonCode").GetString());
        Assert.Equal("batch.finished", rows[6].GetProperty("SafeFields").GetProperty("ReasonCode").GetString());
    }

    private static JsonElement[] Rows(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();

    private static string Export(string text) => string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(Encoding.UTF8.GetBytes(line)))));

    private static string ReadHistory(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class LoggingFixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "cloudscribe-batch-logging-" + Guid.NewGuid().ToString("N"));
        internal IOptions<CloudScribeOptions> Options { get; }
        internal AppPaths Paths { get; }
        internal string History => Path.Combine(Root, "data", "google-trend-history-v1.jsonl");
        internal LoggingFixture()
        {
            Directory.CreateDirectory(Root);
            Options = Microsoft.Extensions.Options.Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = Root });
            Paths = new(Options);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
