using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.Infrastructure.Tests;

public sealed class SafeDiagnosticExportEncoderTests
{
    private const string Private = "synthetic unpublished prose fragment 7391";

    [Theory]
    [InlineData(1011, "InitializerName", "DatabaseInitializer")]
    [InlineData(1500, "Route", "Library")]
    [InlineData(1501, "LifecycleState", "Ready")]
    [InlineData(1502, "Theme", "FollowSystem")]
    [InlineData(1504, "LayoutMode", "Compact")]
    public void KnownDiagnosticContextSurvivesButArbitraryReplacementDoesNot(int eventId, string field, string value)
    {
        byte[] Encode(string candidate) => SafeDiagnosticExportEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2,
            EventId = eventId,
            FieldsComplete = true,
            SafeFields = new Dictionary<string, string>(StringComparer.Ordinal) { [field] = candidate },
        }));
        using var valid = JsonDocument.Parse(Encode(value));
        Assert.Equal(value, valid.RootElement.GetProperty("SafeFields").GetProperty(field).GetString());
        Assert.True(valid.RootElement.GetProperty("FieldsComplete").GetBoolean());
        string rejected = Encoding.UTF8.GetString(Encode(Private));
        Assert.DoesNotContain(Private, rejected, StringComparison.Ordinal);
        using var invalid = JsonDocument.Parse(rejected);
        Assert.False(invalid.RootElement.GetProperty("FieldsComplete").GetBoolean());
    }

    [Fact]
    public void BuildAndTraceIdentitiesAreValidatedWithoutExportingArbitraryText()
    {
        string module = Guid.NewGuid().ToString("D");
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2,
            EventId = 1011,
            FieldsComplete = true,
            SafeFields = new Dictionary<string, string>(StringComparer.Ordinal) { ["InitializerName"] = "DatabaseInitializer" },
            ApplicationVersion = "1.0.1+abcdef123",
            RuntimeVersion = "10.0.14",
            ModuleId = module,
            TraceId = new string('a', 32),
            SpanId = Private,
        });
        byte[] encoded = SafeDiagnosticExportEncoder.Encode(source);
        Assert.DoesNotContain(Private, Encoding.UTF8.GetString(encoded), StringComparison.Ordinal);
        using var output = JsonDocument.Parse(encoded);
        Assert.Equal("1.0.1+abcdef123", output.RootElement.GetProperty("ApplicationVersion").GetString());
        Assert.Equal("10.0.14", output.RootElement.GetProperty("RuntimeVersion").GetString());
        Assert.Equal(module, output.RootElement.GetProperty("ModuleId").GetString());
        Assert.Equal(new string('a', 32), output.RootElement.GetProperty("TraceId").GetString());
        Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("SpanId").ValueKind);
    }

    [Fact]
    public void LegacyAndUnknownFieldsHaveNoRawFallback()
    {
        byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(new { EventId = 1004, Message = Private, Category = Private, ExceptionType = Private, ExceptionDetail = Private });
        string safe = Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(legacy));
        Assert.DoesNotContain(Private, safe, StringComparison.Ordinal);
        Assert.Contains("LegacyMetadataOnly", safe, StringComparison.Ordinal);
        Assert.Contains("LegacyOmitted", safe, StringComparison.Ordinal);
        Assert.DoesNotContain(Private, Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(Encoding.UTF8.GetBytes(Private))), StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentSafeStageSurvivesWhileInjectedTextAndUnrecognizedExceptionAreOmitted()
    {
        var fields = ReviewFields(); fields["InjectedPrivateBody"] = Private;
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(new
        { SchemaVersion = 2, EventId = 6300, FieldsComplete = true, SafeFields = fields, Message = Private, Category = Private, ExceptionDetail = Private });
        string text = Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(source));
        Assert.DoesNotContain(Private, text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("Review", json.RootElement.GetProperty("SafeFields").GetProperty("Stage").GetString());
        Assert.Equal("CapabilityStale", json.RootElement.GetProperty("SafeFields").GetProperty("Reason").GetString());
        Assert.False(json.RootElement.GetProperty("FieldsComplete").GetBoolean());
        Assert.Equal("InvalidOrUnavailable", json.RootElement.GetProperty("ExceptionEvidence").GetString());
    }

    [Fact]
    public void ValidSummaryIsRevalidatedAndPayloadHashesStayLocal()
    {
        string hash = new('a', 64);
        var summary = SafeExceptionSummarizer.Capture(new IOException(Private));
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2,
            EventId = 1605,
            FieldsComplete = true,
            SafeFields = new Dictionary<string, string>(StringComparer.Ordinal) { ["PayloadSha256"] = hash },
            ExceptionDetailFormat = "safe-exception-summary-v1",
            ExceptionDetail = JsonSerializer.Serialize(summary),
        });
        string safe = Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(source));
        Assert.DoesNotContain(hash, safe, StringComparison.Ordinal);
        Assert.DoesNotContain(Private, safe, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(safe);
        Assert.Equal("SafeSummary", json.RootElement.GetProperty("ExceptionEvidence").GetString());
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":2,\"SchemaVersion\":2}")]
    [InlineData("{\"SchemaVersion\":2,\"SafeFields\":{\"Stage\":\"Review\",\"Stage\":\"Review\"}}")]
    [InlineData("<html>synthetic unpublished prose fragment 7391</html>")]
    public void DuplicateAndMalformedRecordsAreOmitted(string input)
    {
        string output = Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(Encoding.UTF8.GetBytes(input)));
        Assert.DoesNotContain(Private, output, StringComparison.Ordinal);
        Assert.Contains("Malformed", output, StringComparison.Ordinal);
        Assert.Contains("omitted", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OversizedAndUnknownSchemaRecordsDoNotExportText()
    {
        using var oversized = JsonDocument.Parse(SafeDiagnosticExportEncoder.Encode(new byte[SafeDiagnosticExportEncoder.MaximumRecordBytes + 1]));
        Assert.Equal("OversizedRecord", oversized.RootElement.GetProperty("RecordState").GetString());
        string unsupported = Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 99, Message = Private })));
        Assert.Contains("UnsupportedSchemaMetadataOnly", unsupported, StringComparison.Ordinal);
        Assert.DoesNotContain(Private, unsupported, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> ReviewFields() => new(StringComparer.Ordinal)
    {
        ["SchemaVersion"] = "1",
        ["Operation"] = Guid.NewGuid().ToString("D"),
        ["Stage"] = "Review",
        ["Reason"] = "CapabilityStale",
        ["ReviewAgeMilliseconds"] = "-1",
        ["ReviewExpiresUtc"] = null,
        ["AdmissionExpiresUtc"] = null,
        ["EvidenceComplete"] = "true",
    };
}
