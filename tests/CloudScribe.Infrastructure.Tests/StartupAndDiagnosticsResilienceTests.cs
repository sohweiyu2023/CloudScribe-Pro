using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace CloudScribe.Infrastructure.Tests;

public sealed class StartupAndDiagnosticsResilienceTests
{
    private static readonly Action<ILogger, string, Exception?> WriteTestInformation = LoggerMessage.Define<string>(
        LogLevel.Information,
        new EventId(9000, "InfrastructureTestDiagnostic"),
        "{Message}");

    [Fact]
    public async Task DiagnosticProviderDegradesInsteadOfThrowingWhenItsDirectoryCannotBeCreated()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string blockedRoot = Path.Combine(temporary, "blocked-root");
        File.WriteAllText(blockedRoot, "not a directory");
        try
        {
            CloudScribeOptions options = new() { AppDataDirectoryOverride = blockedRoot };
            AppPaths paths = new(Options.Create(options));

            using BoundedJsonFileLoggerProvider provider = new(paths, Options.Create(options), TimeProvider.System);
            int statusChanges = 0;
            provider.StatusChanged += (_, _) => Interlocked.Increment(ref statusChanges);
            ILogger logger = provider.CreateLogger("test");
            WriteTestInformation(logger, "This bounded record is deliberately dropped.", null);
            for (int attempt = 0; attempt < 50 && provider.IsAvailable; attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.False(provider.IsAvailable);
            Assert.True(provider.DroppedRecordCount >= 1);
            Assert.Equal(1, Volatile.Read(ref statusChanges));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }


    [Fact]
    public async Task FaultyDiagnosticStatusObserverCannotSuppressLaterObservers()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-diagnostics-observers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string blockedRoot = Path.Combine(temporary, "blocked-root");
        File.WriteAllText(blockedRoot, "not a directory");
        try
        {
            CloudScribeOptions options = new() { AppDataDirectoryOverride = blockedRoot };
            AppPaths paths = new(Options.Create(options));
            using BoundedJsonFileLoggerProvider provider = new(paths, Options.Create(options), TimeProvider.System);
            int healthyObserverCalls = 0;
            provider.StatusChanged += (_, _) => throw new InvalidOperationException("observer failure");
            provider.StatusChanged += (_, _) => Interlocked.Increment(ref healthyObserverCalls);

            WriteTestInformation(provider.CreateLogger("test"), "Trigger bounded diagnostic initialization.", null);
            for (int attempt = 0; attempt < 100 && provider.IsAvailable; attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.False(provider.IsAvailable);
            Assert.Equal(1, Volatile.Read(ref healthyObserverCalls));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public async Task OneFileDiagnosticPolicyReplacesFullCurrentLogInsteadOfDisablingLogging()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-one-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            CloudScribeOptions options = new()
            {
                AppDataDirectoryOverride = temporary,
                DiagnosticFileSizeMiB = 1,
                DiagnosticDirectorySizeMiB = 1,
                DiagnosticMaximumFileCount = 1,
            };
            AppPaths paths = new(Options.Create(options));
            paths.EnsureDiagnosticsDirectory();
            DateTimeOffset now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
            using BoundedJsonFileLoggerProvider provider = new(
                paths,
                Options.Create(options),
                new FixedTimeProvider(now));
            string currentLog = provider.CurrentLogPath;
            await File.WriteAllBytesAsync(
                currentLog,
                new byte[1024 * 1024 + 1],
                TestContext.Current.CancellationToken);
            WriteTestInformation(provider.CreateLogger("test"), "replacement record", null);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (File.Exists(currentLog))
                {
                    long length = new FileInfo(currentLog).Length;
                    if (length > 0 && length < 1024 * 1024)
                    {
                        break;
                    }
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(provider.IsAvailable);
            Assert.True(File.Exists(currentLog));
            Assert.InRange(new FileInfo(currentLog).Length, 1, 1024 * 1024 - 1);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public async Task IncomingRecordRotatesBeforeItCanExceedPerFileCap()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-record-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            CloudScribeOptions options = new()
            {
                AppDataDirectoryOverride = temporary,
                DiagnosticFileSizeMiB = 1,
                DiagnosticDirectorySizeMiB = 2,
                DiagnosticMaximumFileCount = 2,
            };
            AppPaths paths = new(Options.Create(options));
            paths.EnsureDiagnosticsDirectory();
            DateTimeOffset now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
            using BoundedJsonFileLoggerProvider provider = new(paths, Options.Create(options), new FixedTimeProvider(now));
            string currentLog = provider.CurrentLogPath;
            await File.WriteAllBytesAsync(
                currentLog,
                new byte[1024 * 1024 - 1],
                TestContext.Current.CancellationToken);
            WriteTestInformation(provider.CreateLogger("test"), "boundary record", null);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                FileInfo[] files = new DirectoryInfo(paths.DiagnosticsDirectory).GetFiles("cloudscribe-*.jsonl");
                if (files.Length == 2 && files.All(file => file.Length is >= 1 and <= 1024 * 1024))
                {
                    break;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            FileInfo[] boundedFiles = new DirectoryInfo(paths.DiagnosticsDirectory).GetFiles("cloudscribe-*.jsonl");
            Assert.Equal(2, boundedFiles.Length);
            Assert.All(boundedFiles, file => Assert.InRange(file.Length, 1, 1024 * 1024));
            Assert.True(provider.IsAvailable);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeMetadataEntriesDoNotConsumeManagedJsonLogFileLimit()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-log-metadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            CloudScribeOptions options = new()
            {
                AppDataDirectoryOverride = temporary,
                DiagnosticFileSizeMiB = 1,
                DiagnosticDirectorySizeMiB = 1,
                DiagnosticMaximumFileCount = 1,
            };
            AppPaths paths = new(Options.Create(options));
            paths.EnsureDiagnosticsDirectory();
            await File.WriteAllTextAsync(
                Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-bootstrap-20260728.log"),
                "bootstrap",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(paths.DiagnosticsDirectory, "LATEST-BOOTSTRAP-LOG.txt"),
                "bootstrap.log",
                TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.Combine(paths.DiagnosticsDirectory, "build"));

            string currentLogPath;
            bool providerWasAvailable;
            using (BoundedJsonFileLoggerProvider provider = new(paths, Options.Create(options), TimeProvider.System))
            {
                WriteTestInformation(provider.CreateLogger("test"), "metadata entries remain outside the JSONL cap", null);
                currentLogPath = provider.CurrentLogPath;
                providerWasAvailable = provider.IsAvailable;
            }

            // Disposal is the provider's committed-record boundary. Inspecting the asynchronous
            // writer before disposal made this retention-policy test depend on runner scheduling.
            Assert.True(providerWasAvailable);
            Assert.True(File.Exists(currentLogPath));
            Assert.Single(new DirectoryInfo(paths.DiagnosticsDirectory).GetFiles("cloudscribe-*.jsonl"));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public async Task StructuredDiagnosticRecordContainsSessionAndRedactedExceptionDetail()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-diagnostic-shape-" + Guid.NewGuid().ToString("N"));
        try
        {
            CloudScribeOptions options = new() { AppDataDirectoryOverride = temporary };
            AppPaths paths = new(Options.Create(options));
            string currentLogPath;
            string logDirectory;
            using (BoundedJsonFileLoggerProvider provider = new(paths, Options.Create(options), TimeProvider.System))
            {
                ILogger logger = provider.CreateLogger("shape-test");
                WriteTestInformation(
                    logger,
                    "shape record",
                    new InvalidOperationException("api_key=super-secret"));
                currentLogPath = provider.CurrentLogPath;
                logDirectory = provider.LogDirectory;
            }

            // Provider disposal is the committed-record boundary: it completes the queue,
            // waits for the writer and releases the log handle before external readers inspect it.
            Assert.True(File.Exists(currentLogPath));
            string line = Assert.Single(await File.ReadAllLinesAsync(
                currentLogPath,
                TestContext.Current.CancellationToken));
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            Assert.True(root.GetProperty("Sequence").GetInt64() >= 1);
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("ApplicationVersion").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("SessionId").GetString()));
            Assert.Equal(Environment.ProcessId, root.GetProperty("ProcessId").GetInt32());
            Assert.True(root.GetProperty("ManagedThreadId").GetInt32() > 0);
            Assert.Equal("System.InvalidOperationException", root.GetProperty("ExceptionType").GetString());
            Assert.Contains("api_key=[REDACTED]", root.GetProperty("ExceptionDetail").GetString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(logDirectory, "LATEST-LOG.txt")));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public void DatabaseDirectoryCreationDoesNotCreateUnownedDiagnosticOrSupportDirectories()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-path-ownership-" + Guid.NewGuid().ToString("N"));
        try
        {
            CloudScribeOptions options = new() { AppDataDirectoryOverride = temporary };
            AppPaths paths = new(Options.Create(options));

            paths.EnsureDatabaseDirectory();

            Assert.True(Directory.Exists(Path.GetDirectoryName(paths.DatabasePath)));
            Assert.False(Directory.Exists(paths.DiagnosticsDirectory));
            Assert.False(Directory.Exists(paths.SupportBundleStagingDirectory));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
