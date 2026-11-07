using System.Text;
using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class LongAudioBudgetLoggingTests
{
    [Fact]
    public async Task BudgetStopPersistsExactSafeAmountsInRoutineLogExportAndDurableHistory()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-budget-log-synthetic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = root });
            var paths = new AppPaths(options); string log;
            using (var sink = new BoundedJsonFileLoggerProvider(paths, options, TimeProvider.System))
            using (var trend = new GoogleTrendHistoryLoggerProvider(paths, TimeProvider.System))
            using (var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(sink).AddProvider(trend)))
            {
                log = sink.CurrentLogPath;
                var logger = factory.CreateLogger("PRIVATE_CATEGORY_CANARY");
                CloudScribeLog.LongAudioBudgetStop(logger, 2001, 2000);
                trend.RequireReady();
            }
            string line = Assert.Single(await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken), x => !string.IsNullOrWhiteSpace(x));
            using var record = JsonDocument.Parse(line);
            Assert.Equal(1621, record.RootElement.GetProperty("EventId").GetInt32());
            Assert.True(record.RootElement.GetProperty("FieldsComplete").GetBoolean());
            Assert.Equal("2001", record.RootElement.GetProperty("SafeFields").GetProperty("ProvisionUsdCents").GetString());
            Assert.Equal("2000", record.RootElement.GetProperty("SafeFields").GetProperty("CapUsdCents").GetString());
            string exported = Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(Encoding.UTF8.GetBytes(line)));
            string history = await File.ReadAllTextAsync(Path.Combine(root, "data", "google-trend-history-v1.jsonl"), TestContext.Current.CancellationToken);
            Assert.Contains("LongAudioBudgetStop", exported, StringComparison.Ordinal);
            Assert.Contains("LongAudioBudgetStop", history, StringComparison.Ordinal);
            Assert.Contains("2001", history, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_CATEGORY_CANARY", line + exported + history, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
