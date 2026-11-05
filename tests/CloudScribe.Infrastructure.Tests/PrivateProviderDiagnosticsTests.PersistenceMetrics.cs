using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceMetricsMeasureActualRetainedBodyAndRecordWithoutRevealingIt(bool envelope)
    {
        var logger = new PersistenceLogger();
        using var f = new Fixture(privateLogger: logger);
        if (envelope) ArmEnvelope(f); else f.Arm();
        const string explanation = "UNIQUE_SYNTHETIC_PRIVATE_FAILURE_EXPLANATION";
        await f.SendAsync(explanation);
        var record = Assert.Single(f.Store.ReadRecords());
        var evidence = Assert.Single(logger.Entries, x => x.EventId == 1609);
        Assert.Equal(envelope, evidence.Fields["ProtectedEnvelope"]);
        Assert.Equal(Encoding.UTF8.GetByteCount(record.Response?.RedactedBody ?? record.Message), evidence.Fields["RedactedBytes"]);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(record).Length, evidence.Fields["RecordBytes"]);
        Assert.DoesNotContain(explanation, JsonSerializer.Serialize(logger.Entries), StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_SECRET_ACCESS_TOKEN", JsonSerializer.Serialize(logger.Entries), StringComparison.Ordinal);
        Assert.Equal(1, f.Handler.Calls);
    }

    private sealed class PersistenceLogger : ILogger<GooglePrivateErrorDiagnostics>
    {
        public List<(int EventId, Dictionary<string, object?> Fields)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                Entries.Add((eventId.Id, fields.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)));
        }
    }
}
