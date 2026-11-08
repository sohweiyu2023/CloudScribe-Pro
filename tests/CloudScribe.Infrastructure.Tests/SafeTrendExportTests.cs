using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.Infrastructure.Tests;

public sealed class SafeTrendExportTests
{
    [Theory]
    [InlineData(1, "Event", 1647)]
    [InlineData(99, "Event", 1647)]
    [InlineData(1, "PRIVATE_KIND", 1647)]
    [InlineData(1, "Event", 9999)]
    public void TrendFreeTextUnknownFieldsAndUnrecognizedSchemasCannotLeak(int version, string kind, int eventId)
    {
        const string privateText = "PRIVATE_TREND_ERROR_ARTICLE_CREDENTIAL_PATH";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = version, Kind = kind, EventId = eventId, FieldsComplete = true,
            EventName = privateText, Module = privateText, Message = privateText, GapReason = privateText,
            ApplicationVersion = privateText, ComponentBuildId = privateText, TimestampUtc = privateText,
            SafeFields = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["BatchId"] = Guid.NewGuid().ToString("D"), ["ItemId"] = null, ["Operation"] = "ReadPrimary",
                ["FailureCategory"] = privateText, ["LocalHResult"] = "-2147024784", ["UnknownField"] = privateText,
            },
        });
        string text = Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(bytes));
        Assert.DoesNotContain("PRIVATE_", text, StringComparison.Ordinal);
        using var row = JsonDocument.Parse(text);
        Assert.False(row.RootElement.GetProperty("FieldsComplete").GetBoolean());
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Version\":1,\"Kind\":\"Event\"}")]
    [InlineData("{\"Version\":1,\"Kind\":\"Gap\",\"EventName\":\"InterruptedTail\",\"GapReason\":\"PRIVATE_GAP\"}")]
    [InlineData("{\"Version\":1,\"Kind\":\"Event\",\"SafeFields\":{\"PRIVATE\":")]
    public void MalformedTrendRecordsHaveExplicitOmissionsWithoutSourceText(string input)
    {
        string text = Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(Encoding.UTF8.GetBytes(input)));
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        using var row = JsonDocument.Parse(text);
        Assert.False(row.RootElement.GetProperty("FieldsComplete").GetBoolean());
        Assert.Contains("Malformed", row.RootElement.GetProperty("RecordState").GetString(), StringComparison.Ordinal);
    }
}
