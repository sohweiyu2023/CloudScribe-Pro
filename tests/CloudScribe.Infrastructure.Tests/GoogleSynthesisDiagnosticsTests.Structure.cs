using System.Net;
using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleSynthesisDiagnosticsTests
{
    [Theory]
    [InlineData("en-US-PRIVATE-CANARY")]
    [InlineData("cmn-CN-Chirp3-HD-Alnilam\n")]
    public void UnknownVoiceStructureDoesNotRetainLocaleShapedPrivateText(string voice)
    {
        var metrics = GoogleRequestStructureEvidence.Measure(JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = "Synthetic sentence." }, voice = new { name = voice } }));
        Assert.Null(metrics.VoiceName); Assert.False(metrics.Complete);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(metrics), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VoiceName", "en-US-PRIVATE-CANARY")]
    [InlineData("VoiceName", "cmn-CN-Chirp3-HD-Alnilam\n")]
    [InlineData("InputMode", "PRIVATE_CANARY")]
    [InlineData("Histogram", "PRIVATE_CANARY")]
    public async Task StructuralFileSinkRejectsUnboundedStringCanaries(string field, string canary)
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-structure-canary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = root });
            string path;
            using (var provider = new BoundedJsonFileLoggerProvider(new AppPaths(options), options, TimeProvider.System))
            using (var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(provider)))
            {
                path = provider.CurrentLogPath;
                var logger = factory.CreateLogger("Synthetic");
                string correlation = Guid.NewGuid().ToString("N"), payloadHash = new('a', 64);
                CloudScribe.Application.Logging.CloudScribeLog.GoogleInputStructure(logger,
                    correlation, payloadHash,
                    string.Equals(field, "VoiceName", StringComparison.Ordinal) ? canary : "cmn-CN-Chirp3-HD-Alnilam",
                    string.Equals(field, "InputMode", StringComparison.Ordinal) ? canary : "Ssml", 9, 3, 1, 3, 9, 3, 3,
                    string.Equals(field, "Histogram", StringComparison.Ordinal) ? canary : "1,0,0,0,0", true, true, 0);
            }
            string persisted = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("PRIVATE", persisted, StringComparison.Ordinal);
            using var row = JsonDocument.Parse(persisted.Trim());
            Assert.False(row.RootElement.GetProperty("FieldsComplete").GetBoolean());
            Assert.False(row.RootElement.GetProperty("SafeFields").TryGetProperty(field, out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructuralMetricsPreserveUnitsWithoutRetainingSource(bool marked)
    {
        const string text = "PRIVATE第一句。第二句😀！\r\n";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = marked ? (object)new { ssml = "<speak><s>PRIVATE第一句。</s><s>第二句😀！&#xD;\n</s></speak>" } : new { text },
            voice = new { name = "cmn-CN-Chirp3-HD-Alnilam" }
        });
        var metrics = GoogleRequestStructureEvidence.Measure(bytes);
        Assert.True(metrics.Complete); Assert.Equal(2, metrics.Sentences);
        Assert.Equal(text.EnumerateRunes().Count(), metrics.SourceScalars);
        Assert.Equal(marked, metrics.ExplicitSentenceMarkers); Assert.Equal(2, metrics.UpTo32);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(metrics), StringComparison.Ordinal);
        Assert.True(metrics.MaximumSentenceUtf8Bytes > metrics.MaximumSentenceScalars);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"input\":{\"ssml\":\"<!DOCTYPE x SYSTEM 'https://not-allowed.invalid'><speak>&x;</speak>\"},\"voice\":{\"name\":\"cmn-CN-Chirp3-HD-Alnilam\"}}")]
    [InlineData("{\"input\":{\"text\":null},\"voice\":{\"name\":\"private\\nsecret\"}}")]
    public void InvalidStructureIsExplicitlyUnavailableWithoutRawFallback(string payload)
    {
        var metrics = GoogleRequestStructureEvidence.Measure(System.Text.Encoding.UTF8.GetBytes(payload));
        Assert.False(metrics.Complete); Assert.Equal(-1, metrics.Sentences);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(metrics), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuralHistoryJoinsOutcomeAndSurvivesProductionFileReadbackWithoutText()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-structure-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = root });
            string logPath;
            using (var provider = new BoundedJsonFileLoggerProvider(new AppPaths(options), options, TimeProvider.System))
            using (var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(provider)))
            using (var http = new HttpClient(new MetadataHandler(HttpStatusCode.BadRequest, "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"PRIVATE_RESPONSE\"}}")))
            {
                logPath = provider.CurrentLogPath;
                var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), factory.CreateLogger<GoogleGenerationHttpTransport>());
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = "PRIVATE_SOURCE。下一句！" }, voice = new { name = "cmn-CN-Chirp3-HD-Alnilam" } });
                await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "PRIVATE_REFERENCE", payload), TestContext.Current.CancellationToken);
            }
            string persisted = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);
            var rows = persisted.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x)).ToArray();
            try
            {
                var structuralRecord = Assert.Single(rows, x => x.RootElement.GetProperty("EventId").GetInt32() == 1616).RootElement;
                Assert.True(structuralRecord.GetProperty("FieldsComplete").GetBoolean());
                var structure = structuralRecord.GetProperty("SafeFields");
                Assert.Equal("cmn-CN-Chirp3-HD-Alnilam", structure.GetProperty("VoiceName").GetString());
                Assert.Equal("Text", structure.GetProperty("InputMode").GetString());
                Assert.Equal("2,0,0,0,0", structure.GetProperty("Histogram").GetString());
                Assert.Equal("2", structure.GetProperty("Sentences").GetString());
                Assert.Equal("-1", structure.GetProperty("SplitDepth").GetString()); // Explicit unavailable, never fabricated.
                string? correlation = structure.GetProperty("Correlation").GetString();
                Assert.Contains(rows, x => x.RootElement.GetProperty("EventId").GetInt32() == 1608
                    && string.Equals(x.RootElement.GetProperty("SafeFields").GetProperty("Correlation").GetString(), correlation, StringComparison.Ordinal));
                Assert.DoesNotContain("PRIVATE_", persisted, StringComparison.Ordinal);
                Assert.DoesNotContain("SYNTHETIC-PRIVATE-MARKER", persisted, StringComparison.Ordinal);
            }
            finally { foreach (var row in rows) row.Dispose(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
