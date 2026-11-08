using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchLoggingTests
{
    [Fact]
    public void SourceOutcomesSurviveBothSinksAndExportWithoutUnreviewedValues()
    {
        using var fixture = new LoggingFixture();
        string path;
        Guid batch = Guid.NewGuid(), item = Guid.NewGuid();
        using (var ordinary = new BoundedJsonFileLoggerProvider(fixture.Paths, fixture.Options, TimeProvider.System))
        using (var history = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(ordinary).AddProvider(history)))
        {
            path = ordinary.CurrentLogPath;
            var logger = factory.CreateLogger("PRIVATE_SOURCE_PATH_TEXT_CREDENTIAL");
            foreach (var outcome in Enum.GetValues<BatchSourceOutcome>()) BatchDiagnostics.Source(logger, batch, item, outcome);
            BatchDiagnostics.Source(logger, null, item, (BatchSourceOutcome)int.MaxValue);
        }
        string text = File.ReadAllText(path), retained = ReadHistory(fixture.History), exported = Export(text);
        Assert.DoesNotContain("PRIVATE_SOURCE", text + retained + exported, StringComparison.Ordinal);
        foreach (var rows in new[] { Rows(text), Rows(exported), Rows(retained).Where(x => string.Equals(x.GetProperty("Kind").GetString(), "Event", StringComparison.Ordinal)).ToArray() })
        {
            Assert.Equal(Enum.GetValues<BatchSourceOutcome>().Length + 1, rows.Length);
            Assert.All(rows, row => Assert.Equal(1651, row.GetProperty("EventId").GetInt32()));
            foreach (var row in rows[..^1])
            {
                Assert.True(row.GetProperty("FieldsComplete").GetBoolean());
                Assert.Equal(item, row.GetProperty("SafeFields").GetProperty("ItemId").GetGuid());
                Assert.Equal(batch, row.GetProperty("SafeFields").GetProperty("BatchId").GetGuid());
            }
            Assert.Equal(Enum.GetNames<BatchSourceOutcome>(), rows[..^1].Select(row => row.GetProperty("SafeFields").GetProperty("SourceOutcome").GetString()), StringComparer.Ordinal);
            Assert.False(rows[^1].GetProperty("FieldsComplete").GetBoolean());
            Assert.False(rows[^1].GetProperty("SafeFields").TryGetProperty("SourceOutcome", out _));
        }
    }

    [Fact]
    public void FailureStoreActionAndCurrencyMetadataSurviveBothSinksWithoutPrivateText()
    {
        using var fixture = new LoggingFixture();
        string path;
        Guid batch = Guid.NewGuid(), item = Guid.NewGuid();
        var error = new IOException("PRIVATE_BATCH_FAILURE_CREDENTIAL_ARTICLE_PATH", unchecked((int)0x80070070));
        using (var ordinary = new BoundedJsonFileLoggerProvider(fixture.Paths, fixture.Options, TimeProvider.System))
        using (var history = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(ordinary).AddProvider(history)))
        {
            path = ordinary.CurrentLogPath;
            var logger = factory.CreateLogger("PRIVATE_BATCH_FAILURE_CREDENTIAL_ARTICLE_PATH");
            BatchDiagnostics.Failure(logger, batch, item, BatchDiagnosticOperation.WriteManifest, error);
            BatchDiagnostics.Store(logger, batch, BatchDiagnosticOperation.ReadBackup, BatchStoreObservation.LoadedBackup, 42);
            BatchDiagnostics.Action(logger, batch, item, BatchDiagnosticOperation.CheckAgain, BatchDiagnosticPhase.Requested);
            CloudScribeLog.BatchCostDenomination(logger, batch, "SGD", 2);
        }
        string text = File.ReadAllText(path), historyText = ReadHistory(fixture.History), exported = Export(text);
        Assert.DoesNotContain("PRIVATE_BATCH_FAILURE", text + historyText + exported, StringComparison.Ordinal);
        foreach (var rows in new[] { Rows(text), Rows(exported), Rows(historyText).Where(x => string.Equals(x.GetProperty("Kind").GetString(), "Event", StringComparison.Ordinal)).ToArray() })
        {
            Assert.Equal([1647, 1648, 1649, 1650], rows.Select(x => x.GetProperty("EventId").GetInt32()).ToArray());
            Assert.All(rows, row => Assert.True(row.GetProperty("FieldsComplete").GetBoolean()));
            var failure = rows[0].GetProperty("SafeFields");
            Assert.Equal("Storage", failure.GetProperty("FailureCategory").GetString());
            Assert.Equal(error.HResult.ToString(System.Globalization.CultureInfo.InvariantCulture), failure.GetProperty("LocalHResult").GetString());
            Assert.Equal(item, failure.GetProperty("ItemId").GetGuid());
            Assert.Equal("42", rows[1].GetProperty("SafeFields").GetProperty("Revision").GetString());
            Assert.Equal("CheckAgain", rows[2].GetProperty("SafeFields").GetProperty("Operation").GetString());
            Assert.Equal("SGD", rows[3].GetProperty("SafeFields").GetProperty("CurrencyCode").GetString());
            Assert.Equal("2", rows[3].GetProperty("SafeFields").GetProperty("CurrencyScale").GetString());
        }
    }

    [Fact]
    public void UnknownCurrencyAndEnumValuesCannotBecomePrivateLogContent()
    {
        using var fixture = new LoggingFixture();
        string path;
        using (var provider = new BoundedJsonFileLoggerProvider(fixture.Paths, fixture.Options, TimeProvider.System))
        {
            path = provider.CurrentLogPath;
            var logger = provider.CreateLogger("ignored");
            Guid batch = Guid.NewGuid();
            CloudScribeLog.BatchCostDenomination(logger, batch, "PRIVATE_CURRENCY_TEXT", 2);
            CloudScribeLog.BatchAction(logger, null, null, (BatchDiagnosticOperation)int.MaxValue, BatchDiagnosticPhase.Requested);
        }
        string text = File.ReadAllText(path);
        Assert.DoesNotContain("PRIVATE_CURRENCY_TEXT", text + Export(text), StringComparison.Ordinal);
        Assert.All(Rows(text), row => Assert.False(row.GetProperty("FieldsComplete").GetBoolean()));
    }
}
