using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.DependencyInjection;
using CloudScribe.Infrastructure.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleTrendHistoryLoggerTests
{
    [Fact]
    public void RunSummaryAndWaitEventsRemainInSafeHistoryAfterRestart()
    {
        using var fixture = new TrendFixture();
        Guid id = Guid.NewGuid();
        using (var provider = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        {
            var logger = provider.CreateLogger("synthetic-not-retained");
            CloudScribeLog.LongAudioRunSummary(logger, id, CloudScribe.Application.Generation.LongAudioWorkflowStage.Completed,
                1000, 800, false, false, 3, 5, 7, 9, 50);
            CloudScribeLog.LongAudioRunSummaryUnavailable(logger, id, CloudScribe.Application.Generation.LongAudioWorkflowStage.Stopped, -1);
            CloudScribeLog.WholeArticleRunTiming(logger, id, 1000, true, true);
            CloudScribeLog.LongAudioPollDeadline(logger, id, 120, true);
            CloudScribeLog.LongAudioUserStoppedWaiting(logger, id, CloudScribe.Application.Generation.LongAudioWorkflowStage.WaitingForGoogle);
            provider.RequireReady();
        }
        using (var reopened = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        {
            reopened.RequireReady();
            var rows = ReadHistoryLines(fixture.History).Where(x => EventKind(x, "Event"))
                .Select(x => JsonSerializer.Deserialize<JsonElement>(x)).ToArray();
            Assert.Equal([1622, 1623, 1624, 1625, 1626], rows.Select(x => x.GetProperty("EventId").GetInt32()).ToArray());
            Assert.All(rows, x => Assert.True(x.GetProperty("FieldsComplete").GetBoolean()));
            Assert.DoesNotContain("synthetic-not-retained", ReadHistory(fixture.History), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionDependencyInjectionKeepsTraceTrendEventsDespiteOrdinaryWarningFilter()
    {
        using var f = new TrendFixture();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        { ["CloudScribe:AppDataDirectoryOverride"] = f.Root }).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddCloudScribeInfrastructure(config);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<CloudScribe.Application.Diagnostics.IGoogleTrendHistoryHealth>();
        Assert.Same(provider.GetRequiredService<GoogleTrendHistoryLoggerProvider>(), health);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("synthetic-component");
        var state = new List<KeyValuePair<string, object?>>
        { new("JobId", Guid.NewGuid()), new("Ordinal", 1), new("IdempotencyKeySha256", new string('a', 64)), new("PayloadSha256", new string('b', 64)) };
        logger.Log(LogLevel.Debug, new EventId(1613), state, null, static (_, _) => "never retained");
        health.RequireReady();
        Assert.Single(ReadHistoryLines(f.History), x => EventKind(x, "Event"));
    }

    [Fact]
    public void RestartAfterTwentyFourHoursAndRoutineLogRemovalRetainsAllTrendEvents()
    {
        using var f = new TrendFixture();
        var clock = new TrendClock(DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        using (var first = new GoogleTrendHistoryLoggerProvider(f.Paths, clock))
        {
            first.RequireReady();
            for (int i = 0; i < 80; i++) Identity(first.CreateLogger("private-category-not-retained"), i);
            Assert.Equal(81, first.RecordsWritten);
        }
        string before = ReadHistory(f.History);
        string debug = Path.Combine(f.Root, "logs", "synthetic-debug.jsonl"); Directory.CreateDirectory(Path.GetDirectoryName(debug)!);
        File.WriteAllText(debug, "synthetic unrelated rotating log"); File.Delete(debug);
        using (var next = new GoogleTrendHistoryLoggerProvider(f.Paths, new TrendClock(clock.GetUtcNow().AddDays(3))))
        {
            next.RequireReady(); Identity(next.CreateLogger("ignored"), 80);
        }
        string after = ReadHistory(f.History);
        Assert.StartsWith(before, after, StringComparison.Ordinal);
        Assert.Equal(81, ReadHistoryLines(f.History).Count(x => EventKind(x, "Event")));
    }

    [Fact]
    public void SchemaCaptureNeverInvokesFormatterOrStoresExceptionMessageOrUnknownCanary()
    {
        using var f = new TrendFixture(); using var provider = new GoogleTrendHistoryLoggerProvider(f.Paths, TimeProvider.System);
        var logger = provider.CreateLogger("SYNTHETIC_SECRET_CANARY");
        var state = new List<KeyValuePair<string, object?>>
        {
            new("JobId", Guid.NewGuid()), new("Ordinal", 1), new("IdempotencyKeySha256", new string('a', 64)),
            new("PayloadSha256", new string('b', 64)), new("UnknownPrivateField", "SYNTHETIC_SECRET_CANARY"),
            new("{OriginalFormat}", "SYNTHETIC_SECRET_CANARY")
        };
        var exception = new IOException("SYNTHETIC_SECRET_CANARY");
        logger.Log(LogLevel.Information, new EventId(1613), state, exception,
            static (_, _) => throw new InvalidOperationException("Formatter must never be called."));
        logger.Log(LogLevel.Information, new EventId(9999), "SYNTHETIC_SECRET_CANARY", null, static (_, _) => string.Empty);
        string raw = ReadHistory(f.History);
        Assert.DoesNotContain("SYNTHETIC_SECRET_CANARY", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Message\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        using var line = JsonDocument.Parse(ReadHistoryLines(f.History).Last());
        Assert.False(line.RootElement.GetProperty("FieldsComplete").GetBoolean());
        Assert.Equal(4, line.RootElement.GetProperty("SafeFields").EnumerateObject().Count());
        Assert.True(provider.IsAvailable);
    }

    [Fact]
    public void InterruptedTailIsPreservedAndFollowedByExplicitDurableGap()
    {
        using var f = new TrendFixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.History)!);
        File.WriteAllText(f.History, "{\"Version\":");
        using var provider = new GoogleTrendHistoryLoggerProvider(f.Paths, TimeProvider.System);
        provider.RequireReady(); Assert.True(provider.HasPartialTail);
        var lines = ReadHistoryLines(f.History); Assert.Equal("{\"Version\":", lines[0]);
        using var gap = JsonDocument.Parse(lines[1]);
        Assert.Equal("Gap", gap.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("PreviousTailIncomplete", gap.RootElement.GetProperty("GapReason").GetString());
        Assert.False(gap.RootElement.GetProperty("FieldsComplete").GetBoolean());
        Identity(provider.CreateLogger("ignored"), 1);
        Assert.True(EventKind(ReadHistoryLines(f.History).Last(), "Event"));
    }

    [Fact]
    public void RealOpenFailureExposesFixedHealthAndSystemCodeWithoutFallback()
    {
        using var f = new TrendFixture(); File.WriteAllText(Path.Combine(f.Root, "data"), "synthetic occupied directory path");
        using var provider = new GoogleTrendHistoryLoggerProvider(f.Paths, TimeProvider.System);
        Assert.False(provider.IsAvailable); Assert.Equal("OpenFailed", provider.FailureReason); Assert.NotNull(provider.LastFailureHResult);
        Assert.Throws<InvalidOperationException>(provider.RequireReady);
        Assert.False(File.Exists(f.History));
    }

    [Fact]
    public void WriteFailureMakesRequiredHealthUnavailableAndDoesNotThrowThroughLogging()
    {
        using var f = new TrendFixture(); using var provider = new GoogleTrendHistoryLoggerProvider(f.Paths, TimeProvider.System);
        var stream = (FileStream)typeof(GoogleTrendHistoryLoggerProvider).GetField("stream",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(provider)!;
        stream.Dispose();
        Identity(provider.CreateLogger("ignored"), 1);
        Assert.False(provider.IsAvailable); Assert.Equal("WriteFailed", provider.FailureReason); Assert.NotNull(provider.LastFailureHResult);
        Assert.Throws<InvalidOperationException>(provider.RequireReady);
    }

    [Fact]
    public void SecondWriterCannotClaimAnAlreadyOwnedHistory()
    {
        using var f = new TrendFixture(); using var first = new GoogleTrendHistoryLoggerProvider(f.Paths, TimeProvider.System);
        using var second = new GoogleTrendHistoryLoggerProvider(f.Paths, TimeProvider.System);
        first.RequireReady(); Assert.False(second.IsAvailable); Assert.Equal("OpenFailed", second.FailureReason);
        Identity(first.CreateLogger("ignored"), 1); Assert.True(first.IsAvailable);
    }

    private static string ReadHistory(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] ReadHistoryLines(string path) => ReadHistory(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void Identity(ILogger logger, int ordinal)
    {
        var job = Guid.NewGuid(); string key = new('a', 64), payload = new('b', 64);
        CloudScribeLog.ArticleRequestIdentity(logger, job, ordinal, key, payload);
    }

    private static bool EventKind(string line, string kind)
    {
        using var json = JsonDocument.Parse(line);
        return string.Equals(json.RootElement.GetProperty("Kind").GetString(), kind, StringComparison.Ordinal);
    }

    private sealed class TrendClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TrendFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "cloudscribe-trend-synthetic-" + Guid.NewGuid().ToString("N"));
        internal AppPaths Paths { get; }
        internal string History => Path.Combine(Root, "data", "google-trend-history-v1.jsonl");
        internal TrendFixture()
        {
            Directory.CreateDirectory(Root);
            Paths = new(Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = Root }));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
