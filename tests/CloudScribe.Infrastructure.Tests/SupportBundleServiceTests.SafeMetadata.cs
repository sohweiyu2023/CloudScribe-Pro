using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class SupportBundleServiceTests
{
    private const string PrivateSentinel = "synthetic unpublished prose fragment 7391";

    [Theory]
    [InlineData(CompletionProtocolCode.DiagnosticOwnedClaim)]
    [InlineData(CompletionProtocolCode.ClosedDatabaseRequired)]
    [InlineData(CompletionProtocolCode.Oversized)]
    [InlineData(CompletionProtocolCode.DiagnosticUnusedRenewal)]
    [InlineData(CompletionProtocolCode.PreviousGatePin)]
    [InlineData(CompletionProtocolCode.MutablePreviousGatePath)]
    [InlineData(CompletionProtocolCode.PredecessorSnapshotChanged)]
    [InlineData(CompletionProtocolCode.PredecessorSnapshotLocation)]
    [InlineData(CompletionProtocolCode.ReleasedDiagnosticState)]
    [InlineData(CompletionProtocolCode.ReleasedManualInventory)]
    [InlineData(CompletionProtocolCode.ClockRollback)]
    [InlineData(CompletionProtocolCode.ClockDiscontinuity)]
    [InlineData(CompletionProtocolCode.WindowExpired)]
    public async Task CompletionProtocolCodeSurvivesActualFileAndExportWithoutExceptionProse(CompletionProtocolCode code)
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = temporary.Path });
        string logPath;
        using (var provider = new BoundedJsonFileLoggerProvider(paths, options, TimeProvider.System))
        using (var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(provider)))
        {
            logPath = provider.CurrentLogPath;
            var component = code is CompletionProtocolCode.ReleasedDiagnosticState or CompletionProtocolCode.ReleasedManualInventory
                ? CompletionProtocolFailureComponent.Successor : CompletionProtocolFailureComponent.Register;
            var expected = new CompletionGateException(CompletionGateFailure.PinMismatch, code, CompletionProtocolExceptionType.TypeError, component, 431);
            var gate = new CompletionWorkflowGate(CompletionGatePayloadTests.Configuration, new CompletionFailureRunner(expected), factory.CreateLogger<CompletionWorkflowGate>());
            Assert.Same(expected, Assert.Throws<CompletionGateException>(gate.RequireReady));
            GoogleCatalogRefreshDiagnostics.Failure(factory.CreateLogger("synthetic-refresh"), Guid.NewGuid(), GoogleCatalogRefreshPhase.AccountLookup, expected);
            GoogleCatalogRefreshDiagnostics.Failure(factory.CreateLogger("synthetic-refresh"), Guid.NewGuid(), GoogleCatalogRefreshPhase.UiBootstrap, expected);
            var unexpected = new IOException(PrivateSentinel);
            var other = new CompletionWorkflowGate(CompletionGatePayloadTests.Configuration, new CompletionFailureRunner(unexpected), factory.CreateLogger<CompletionWorkflowGate>());
            Assert.Same(unexpected, Assert.Throws<IOException>(other.RequireReady));
        }
        string text = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);
        Assert.Contains(code.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("AccountLookup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UiBootstrap", text, StringComparison.Ordinal);
        Assert.Contains("TypeError", text, StringComparison.Ordinal);
        Assert.Contains("431", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateSentinel, text, StringComparison.Ordinal);
        string exported = string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(System.Text.Encoding.UTF8.GetBytes(line)))));
        Assert.Contains(code.ToString(), exported, StringComparison.Ordinal);
        if (code is CompletionProtocolCode.ReleasedDiagnosticState or CompletionProtocolCode.ReleasedManualInventory)
            Assert.Contains("Successor", exported, StringComparison.Ordinal);
        Assert.Contains("AccountLookup", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("UiBootstrap", exported, StringComparison.Ordinal);
        Assert.Contains("TypeError", exported, StringComparison.Ordinal);
        Assert.Contains("431", exported, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateSentinel, exported, StringComparison.Ordinal);
    }

    private sealed class CompletionFailureRunner(Exception failure) : ICompletionGateCommandRunner
    {
        public void Run(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt) => throw failure;
    }

    [Fact]
    public async Task ExportRegeneratesSafeMetadataAndPreservesPrivateLegacyOriginals()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string legacy = Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-" + PrivateSentinel + ".jsonl");
        string boot = Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-bootstrap-legacy.log");
        await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new { EventId = 1004, Message = PrivateSentinel, ExceptionDetail = PrivateSentinel }) + "\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(boot, "2026-01-01\tprocess.fatal-startup\t" + PrivateSentinel + "\n" + PrivateSentinel + "\n", TestContext.Current.CancellationToken);
        byte[] beforeLegacy = SHA256.HashData(await File.ReadAllBytesAsync(legacy, TestContext.Current.CancellationToken));
        byte[] beforeBoot = SHA256.HashData(await File.ReadAllBytesAsync(boot, TestContext.Current.CancellationToken));
        var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = temporary.Path });
        using (var provider = new BoundedJsonFileLoggerProvider(paths, options, TimeProvider.System))
            _ = LocalRequestDiagnostics.Failure(provider.CreateLogger("synthetic-local-only"), Guid.NewGuid(), LocalRequestStage.DocumentSave,
                new IOException(PrivateSentinel), DateTimeOffset.UtcNow);
        string archivePath = await CreateService(paths, 8).CreateAsync(Path.Combine(temporary.Path, "exports"), TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(archivePath);
        string all = string.Empty;
        foreach (var entry in archive.Entries)
        {
            Assert.DoesNotContain(PrivateSentinel, entry.FullName, StringComparison.Ordinal);
            using var reader = new StreamReader(entry.Open());
            all += await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        }
        Assert.DoesNotContain(PrivateSentinel, all, StringComparison.Ordinal);
        Assert.Contains("DocumentSave", all, StringComparison.Ordinal);
        Assert.Contains("LegacyOmitted", all, StringComparison.Ordinal);
        Assert.Equal(beforeLegacy, SHA256.HashData(await File.ReadAllBytesAsync(legacy, TestContext.Current.CancellationToken)));
        Assert.Equal(beforeBoot, SHA256.HashData(await File.ReadAllBytesAsync(boot, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task LoggerFailureAfterArchiveCommitCannotHideTheCreatedArchive()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        var service = new SupportBundleService(paths, Options.Create(new CloudScribeOptions { SupportBundleMaximumMiB = 8 }), TimeProvider.System, new FailingBundleLogger());
        string result = await service.CreateAsync(Path.Combine(temporary.Path, "exports"), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(result));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(temporary.Path, "exports")), path => path.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownFormatterIsNotExecutedAndItsPrivateStateDoesNotEnterTheSink()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string log;
        using (var provider = new BoundedJsonFileLoggerProvider(paths, Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = temporary.Path }), TimeProvider.System))
        {
            var logger = provider.CreateLogger(PrivateSentinel);
            logger.Log(LogLevel.Error, new EventId(9999, PrivateSentinel), PrivateSentinel, new IOException(PrivateSentinel),
                (_, _) => throw new InvalidOperationException("Formatter must not execute."));
            log = provider.CurrentLogPath;
        }
        string content = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(PrivateSentinel, content, StringComparison.Ordinal);
        Assert.Contains("UnregisteredEvent", content, StringComparison.Ordinal);
        Assert.Contains("safe-exception-summary-v1", content, StringComparison.Ordinal);
    }

    private sealed class FailingBundleLogger : ILogger<SupportBundleService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => throw new IOException("Synthetic observer failure.");
    }
}
