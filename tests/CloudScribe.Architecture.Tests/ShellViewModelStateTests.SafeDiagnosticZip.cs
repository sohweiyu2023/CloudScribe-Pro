using System.IO.Compression;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task SafeHistoryUiCommandCreatesTheRealSanitizedZipWithoutChangingSources()
    {
        using var fixture = new SafeDiagnosticUiFixture();
        string log = Path.Combine(fixture.Paths.DiagnosticsDirectory, "cloudscribe-private-synthetic.log");
        const string privateText = "PRIVATE_SYNTHETIC_CAPTURE_AND_DOCUMENT_CONTENT";
        await File.WriteAllTextAsync(log, privateText + "\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(fixture.Paths.RootDirectory, "private-provider-capture.json"), privateText, TestContext.Current.CancellationToken);
        Guid batch = Guid.NewGuid();
        using (var history = new GoogleTrendHistoryLoggerProvider(fixture.Paths, TimeProvider.System))
        {
            var logger = history.CreateLogger("synthetic-ui-test");
            CloudScribeLog.BatchItemCompleted(logger, batch, 1, 123, 456);
        }
        string historyPath = Path.Combine(fixture.Paths.RootDirectory, "data", "google-trend-history-v1.jsonl");
        string historyBefore = await File.ReadAllTextAsync(historyPath, TestContext.Current.CancellationToken);
        using ShellViewModel model = CreateViewModel(supportBundles: fixture.Service);
        model.ConfigureSafeDiagnosticExport(fixture.Paths.SupportBundleStagingDirectory);
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        string bundle = Assert.Single(Directory.EnumerateFiles(fixture.Paths.SupportBundleStagingDirectory, "*.zip"));
        Assert.Contains(bundle, model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        using var archive = ZipFile.OpenRead(bundle);
        Assert.Contains(archive.Entries, entry => string.Equals(entry.FullName, "history/google-trend-history.jsonl", StringComparison.Ordinal));
        string all = string.Empty;
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            all += await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        }
        Assert.Contains(batch.ToString("D"), all, StringComparison.Ordinal);
        Assert.Contains("BatchItemCompleted", all, StringComparison.Ordinal);
        Assert.DoesNotContain(privateText, all, StringComparison.Ordinal);
        Assert.Equal(privateText + "\n", await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken));
        Assert.Equal(historyBefore, await File.ReadAllTextAsync(historyPath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Paths.SupportBundleStagingDirectory), file => file.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SafeHistoryUiSourceFailureLeavesNoZipOrPartialAndDoesNotExposeExceptionText()
    {
        using var fixture = new SafeDiagnosticUiFixture();
        string log = Path.Combine(fixture.Paths.DiagnosticsDirectory, "cloudscribe-locked.jsonl");
        await File.WriteAllTextAsync(log, "{}\n", TestContext.Current.CancellationToken);
        using var locked = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ShellViewModel model = CreateViewModel(supportBundles: fixture.Service);
        model.ConfigureSafeDiagnosticExport(fixture.Paths.SupportBundleStagingDirectory);
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        Assert.Contains("Export failed", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(log, model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.SupportBundleStagingDirectory));
        Assert.Equal(3, locked.Length);
        Assert.True(model.CanExportSafeDiagnosticHistory);
    }

    [Fact]
    public async Task LoggingObserverFailureCannotEscapeSafeHistoryUiFailureHandling()
    {
        var service = new SafeHistoryExportStub { Export = (_, _) => Task.FromException<string>(new IOException("PRIVATE_FAILURE")) };
        using ShellViewModel model = CreateViewModel(logger: new SafeHistoryFailingLogger(), supportBundles: service);
        model.ConfigureSafeDiagnosticExport(SafeHistoryDestination);
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        Assert.Contains("Export failed", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_FAILURE", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.True(model.CanExportSafeDiagnosticHistory);
    }

    private sealed class SafeDiagnosticUiFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudscribe-safe-history-ui-" + Guid.NewGuid().ToString("N"));
        internal AppPaths Paths { get; }
        internal SupportBundleService Service { get; }
        internal SafeDiagnosticUiFixture()
        {
            var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = _root });
            Paths = new AppPaths(options);
            Paths.EnsureDirectories();
            Service = new SupportBundleService(Paths, options, TimeProvider.System, NullLogger<SupportBundleService>.Instance);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class SafeHistoryFailingLogger : ILogger<ShellViewModel>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new IOException("Synthetic logger failure.");
    }
}
