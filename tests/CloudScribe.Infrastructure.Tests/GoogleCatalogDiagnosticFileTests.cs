using System.Net;
using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleCatalogDiagnosticFileTests
{
    [Fact]
    public async Task ActualBoundedFileSinkRetainsUsefulStageAndStatusWithoutPrivateException()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-catalog-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = temporary });
            string logPath;
            using (var provider = new BoundedJsonFileLoggerProvider(new AppPaths(options), options, TimeProvider.System))
            {
                var logger = provider.CreateLogger("catalog-test");
                Guid id = Guid.NewGuid();
                var exception = new HttpRequestException("Bearer secret-token private-article-body", null, HttpStatusCode.Forbidden);
                exception.Data["private-key"] = "private-account-path";
                GoogleCatalogRefreshDiagnostics.Phase(logger, id, GoogleCatalogRefreshPhase.CatalogRequest);
                GoogleCatalogRefreshDiagnostics.Http(logger, id, GoogleCatalogRefreshPhase.CatalogRequest, HttpStatusCode.Forbidden);
                GoogleCatalogRefreshDiagnostics.Failure(logger, id, GoogleCatalogRefreshPhase.CatalogRequest, exception);
                logPath = provider.CurrentLogPath;
            }

            string[] lines = await File.ReadAllLinesAsync(logPath, TestContext.Current.CancellationToken);
            Assert.Equal(3, lines.Length);
            Assert.Contains(lines, line => line.Contains("CatalogRequest", StringComparison.Ordinal) && line.Contains("403", StringComparison.Ordinal));
            foreach (string line in lines)
            {
                Assert.True(line.Length < 4096);
                using var document = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("ExceptionType").ValueKind);
                Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("ExceptionDetail").ValueKind);
                foreach (string secret in new[] { "secret-token", "private-article-body", "private-key", "private-account-path" })
                    Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
            }
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }
}
