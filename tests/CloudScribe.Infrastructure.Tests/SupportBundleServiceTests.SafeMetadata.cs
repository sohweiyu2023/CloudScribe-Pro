using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class SupportBundleServiceTests
{
    private const string PrivateSentinel = "synthetic unpublished prose fragment 7391";

    [Fact]
    public async Task CompletionProtocolCodeSurvivesActualFileAndExportWithoutExceptionProse()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = temporary.Path });
        string logPath;
        using (var provider = new BoundedJsonFileLoggerProvider(paths, options, TimeProvider.System))
        using (var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(provider)))
        {
            logPath = provider.CurrentLogPath;
            var expected = new CompletionGateException(CompletionGateFailure.PinMismatch, CompletionProtocolCode.DiagnosticOwnedClaim, CompletionProtocolExceptionType.TypeError, CompletionProtocolFailureComponent.Register, 431);
            var gate = new CompletionWorkflowGate(CompletionGatePayloadTests.Configuration, new CompletionFailureRunner(expected), factory.CreateLogger<CompletionWorkflowGate>());
            Assert.Same(expected, Assert.Throws<CompletionGateException>(gate.RequireReady));
            var unexpected = new IOException(PrivateSentinel);
            var other = new CompletionWorkflowGate(CompletionGatePayloadTests.Configuration, new CompletionFailureRunner(unexpected), factory.CreateLogger<CompletionWorkflowGate>());
            Assert.Same(unexpected, Assert.Throws<IOException>(other.RequireReady));
        }
        string text = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);
        Assert.Contains("DiagnosticOwnedClaim", text, StringComparison.Ordinal);
        Assert.Contains("TypeError", text, StringComparison.Ordinal);
        Assert.Contains("431", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateSentinel, text, StringComparison.Ordinal);
        string exported = string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Encoding.UTF8.GetString(SafeDiagnosticExportEncoder.Encode(System.Text.Encoding.UTF8.GetBytes(line)))));
        Assert.Contains("DiagnosticOwnedClaim", exported, StringComparison.Ordinal);
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
