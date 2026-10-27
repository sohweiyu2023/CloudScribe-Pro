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
    [Fact]
    public async Task ProductionBoundedFileSinkRetainsSafeDiagnosticEventsOnlyInIsolatedProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        string full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("cloudscribe-diagnostic-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected synthetic diagnostic root.");
        try
        {
            var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = root });
            var paths = new AppPaths(options);
            string logPath;
            using (var provider = new BoundedJsonFileLoggerProvider(paths, options, TimeProvider.System))
            using (var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(provider)))
            using (var http = new HttpClient(new MetadataHandler(HttpStatusCode.BadRequest,
                "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"This request contains sentences that are too long. Read more PRIVATE_RESPONSE_TOKEN\"}}")))
            {
                logPath = provider.CurrentLogPath;
                var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), factory.CreateLogger<GoogleGenerationHttpTransport>());
                await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
            } // Ordinary disposal drains the bounded file sink without a live app/profile.
            string text = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);
            var ids = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("EventId").GetInt32(); }).ToArray();
            Assert.Contains(1605, ids); Assert.Contains(1606, ids); Assert.Contains(1607, ids);
            Assert.Contains("SentenceLengthReported", text, StringComparison.Ordinal);
            Assert.Contains("Absent", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC-PRIVATE-MARKER", text, StringComparison.Ordinal);
            Assert.DoesNotContain("https://", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
