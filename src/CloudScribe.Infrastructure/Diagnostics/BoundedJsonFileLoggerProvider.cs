using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Diagnostics;

public sealed class BoundedJsonFileLoggerProvider : ILoggerProvider, ISupportExternalScope, IDiagnosticLogStatus
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string ApplicationVersion = ResolveApplicationVersion();
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CancelledShutdownTimeout = TimeSpan.FromSeconds(2);
    private const int MaximumAuxiliaryDiagnosticEntries = 64;
    private readonly AppPaths _paths;
    private readonly CloudScribeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<DiagnosticLogRecord> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, BoundedJsonLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Task _writerTask;
    private readonly DateTimeOffset _sessionStartedAtUtc;
    private readonly string _sessionId;
    private readonly string _currentLogFileName;
    private int _writerAvailable;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private long _droppedRecordCount;
    private long _recordSequence;
    private int _latestPointerWritten;
    private int _disposed;

    public BoundedJsonFileLoggerProvider(
        AppPaths paths,
        IOptions<CloudScribeOptions> options,
        TimeProvider timeProvider)
    {
        _paths = paths;
        _options = options.Value;
        _timeProvider = timeProvider;
        _sessionStartedAtUtc = _timeProvider.GetUtcNow();
        _sessionId = Guid.NewGuid().ToString("N");
        _currentLogFileName = $"cloudscribe-{_sessionStartedAtUtc:yyyyMMdd-HHmmssfff}-p{Environment.ProcessId}-{_sessionId}.jsonl";
        _channel = Channel.CreateBounded<DiagnosticLogRecord>(new BoundedChannelOptions(_options.DiagnosticQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        // Provider construction performs no filesystem work. Diagnostics initialize on the
        // background writer so an unavailable path can never delay or prevent app startup.
        _writerAvailable = 1;
        _writerTask = Task.Run(() => WriteLoopAsync(_shutdown.Token), CancellationToken.None);
    }

    public event EventHandler? StatusChanged;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new BoundedJsonLogger(category, this));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    internal IExternalScopeProvider ScopeProvider => _scopeProvider;

    public bool IsAvailable => Volatile.Read(ref _writerAvailable) != 0;

    public string LogDirectory => _paths.DiagnosticsDirectory;

    public string CurrentLogPath => Path.Combine(LogDirectory, _currentLogFileName);

    public long DroppedRecordCount => Interlocked.Read(ref _droppedRecordCount);

    internal void Enqueue(
        LogLevel logLevel,
        EventId eventId,
        string category,
        string message,
        Exception? exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!IsAvailable)
        {
            Interlocked.Increment(ref _droppedRecordCount);
            return;
        }

        Activity? activity = Activity.Current;
        bool accepted = _channel.Writer.TryWrite(new DiagnosticLogRecord(
            Interlocked.Increment(ref _recordSequence),
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ApplicationVersion,
            _sessionId,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            logLevel.ToString(),
            eventId.Id,
            DiagnosticRedactor.Sanitize(eventId.Name),
            DiagnosticRedactor.Sanitize(category),
            DiagnosticRedactor.Sanitize(message),
            exception?.GetType().FullName,
            GetExceptionDetail(exception),
            activity?.TraceId.ToString(),
            activity?.SpanId.ToString()));
        if (!accepted)
        {
            Interlocked.Increment(ref _droppedRecordCount);
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (DiagnosticLogRecord record in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await WriteRecordAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRecoverableDiagnosticException(exception))
                {
                    // Fail closed for diagnostics while keeping user work alive. The current and
                    // already-queued records are counted once and discarded without further I/O.
                    DisableAndDrain(currentRecordWasDropped: true);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!IsFatalDiagnosticException(exception))
        {
            // Diagnostics are explicitly non-fatal. Unexpected serializer/runtime failures must
            // still make health truthful and discard queued diagnostics without further I/O.
            DisableAndDrain(currentRecordWasDropped: false);
        }
    }

    private void DisableAndDrain(bool currentRecordWasDropped)
    {
        MarkUnavailable();
        if (currentRecordWasDropped)
        {
            Interlocked.Increment(ref _droppedRecordCount);
        }

        while (_channel.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref _droppedRecordCount);
        }
    }

    private static bool IsFatalDiagnosticException(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private void MarkUnavailable()
    {
        if (Interlocked.Exchange(ref _writerAvailable, 0) == 0)
        {
            return;
        }

        EventHandler? handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception) when (!IsFatalDiagnosticException(exception))
            {
                // Each diagnostic health observer is advisory. One faulty observer cannot
                // suppress later observers or fault the background writer.
            }
        }
    }


    private static string ResolveApplicationVersion() =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

    private static string? GetExceptionDetail(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        try
        {
            return DiagnosticRedactor.Sanitize(exception.ToString());
        }
        catch (Exception detailException) when (!IsFatalDiagnosticException(detailException))
        {
            return DiagnosticRedactor.Sanitize(exception.GetType().FullName);
        }
    }

    private static bool IsRecoverableDiagnosticException(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or SecurityException
        or JsonException
        or NotSupportedException
        or ArgumentException;

    private async Task WriteRecordAsync(
        DiagnosticLogRecord record,
        CancellationToken cancellationToken)
    {
        _paths.EnsureDiagnosticsDirectory();
        EnsureDiagnosticsDirectoryIsPhysical();
        string path = GetCurrentLogPath();
        EnsureLatestLogPointer(path);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(record);
        long incomingLength = checked(payload.LongLength + NewLine.LongLength);
        long fileCap = _options.DiagnosticFileSizeMiB * 1024L * 1024L;
        if (incomingLength > fileCap)
        {
            throw new IOException("A single bounded diagnostic record exceeds the configured file-size cap.");
        }

        DiagnosticDirectorySnapshot snapshot = EnumerateDiagnosticFilesBounded();
        EnsureCurrentLogIsPhysical(path);
        EnsureCapacityForCurrentLog(path, snapshot, incomingLength);
        // Entry-cap enforcement may delete a stale or full file. Re-enumerate before
        // projected byte accounting so FileInfo objects never describe deleted entries.
        snapshot = EnumerateDiagnosticFilesBounded();
        EnsureDirectoryCapacityForIncoming(snapshot, incomingLength);
        RotateIfRequired(path, incomingLength);
        FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        EnforceDirectoryCap();
    }

    private string GetCurrentLogPath() => CurrentLogPath;

    private void EnsureLatestLogPointer(string logPath)
    {
        if (Interlocked.CompareExchange(ref _latestPointerWritten, 1, 0) != 0)
        {
            return;
        }

        string pointerPath = Path.Combine(_paths.DiagnosticsDirectory, "LATEST-LOG.txt");
        string stagingPath = pointerPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            EnsureCurrentLogIsPhysical(pointerPath);
            using (FileStream stream = new(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 256,
                       FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream, Utf8WithoutBom, bufferSize: 256, leaveOpen: false))
            {
                writer.WriteLine(Path.GetFileName(logPath));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(stagingPath, pointerPath, overwrite: true);
        }
        catch
        {
            Volatile.Write(ref _latestPointerWritten, 0);
            throw;
        }
        finally
        {
            try
            {
                File.Delete(stagingPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RotateIfRequired(string path, long incomingLength)
    {
        long cap = _options.DiagnosticFileSizeMiB * 1024L * 1024L;
        if (!File.Exists(path) || new FileInfo(path).Length <= cap - incomingLength)
        {
            return;
        }

        string rotated = Path.Combine(
            _paths.DiagnosticsDirectory,
            $"cloudscribe-{_sessionStartedAtUtc:yyyyMMdd-HHmmssfff}-p{Environment.ProcessId}-{_sessionId}-rotated-{Guid.NewGuid():N}.jsonl");
        File.Move(path, rotated, overwrite: false);
    }

    private void EnsureDirectoryCapacityForIncoming(
        DiagnosticDirectorySnapshot snapshot,
        long incomingLength)
    {
        long cap = _options.DiagnosticDirectorySizeMiB * 1024L * 1024L;
        long total = snapshot.Files.Aggregate(0L, static (sum, file) => checked(sum + file.Length));
        if (total <= cap - incomingLength)
        {
            return;
        }

        foreach (FileInfo file in snapshot.Files.OrderBy(static file => file.LastWriteTimeUtc))
        {
            try
            {
                long length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (total <= cap - incomingLength)
            {
                return;
            }
        }

        throw new IOException(
            $"Diagnostic logging cannot reserve {incomingLength} bytes within its {cap}-byte directory cap; logging is paused.");
    }

    private void EnforceDirectoryCap()
    {
        long cap = _options.DiagnosticDirectorySizeMiB * 1024L * 1024L;
        DiagnosticDirectorySnapshot snapshot = EnumerateDiagnosticFilesBounded();
        List<FileInfo> files = snapshot.Files
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ToList();
        long total = files.Sum(static file => file.Length);

        for (int index = files.Count - 1; index >= 0 && total > cap; index--)
        {
            FileInfo file = files[index];
            try
            {
                long length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (total > cap)
        {
            throw new IOException(
                $"Diagnostic logging cannot enforce its {cap}-byte directory cap; logging is paused.");
        }
    }

    private DiagnosticDirectorySnapshot EnumerateDiagnosticFilesBounded()
    {
        int maximumManagedFileCount = _options.DiagnosticMaximumFileCount;
        int maximumScannedEntryCount = checked(maximumManagedFileCount + MaximumAuxiliaryDiagnosticEntries);
        int scannedEntryCount = 0;
        List<FileInfo> files = new(maximumManagedFileCount);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (FileSystemInfo entry in new DirectoryInfo(_paths.DiagnosticsDirectory)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            scannedEntryCount++;
            if (scannedEntryCount > maximumScannedEntryCount)
            {
                throw new IOException(
                    $"Diagnostic logging is paused because more than {maximumScannedEntryCount} top-level entries exist.");
            }

            if (entry is FileInfo file
                && file.Name.StartsWith("cloudscribe-", comparison)
                && file.Name.EndsWith(".jsonl", comparison)
                && file.LinkTarget is null
                && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                files.Add(file);
            }
        }

        return new DiagnosticDirectorySnapshot(files);
    }

    private void EnsureCapacityForCurrentLog(
        string path,
        DiagnosticDirectorySnapshot snapshot,
        long incomingLength)
    {
        FileInfo current = new(path);
        long fileCap = _options.DiagnosticFileSizeMiB * 1024L * 1024L;
        bool requiresNewManagedFile = !current.Exists || current.Length > fileCap - incomingLength;
        int filesToDelete = snapshot.Files.Count - _options.DiagnosticMaximumFileCount + 1;
        if (!requiresNewManagedFile || filesToDelete <= 0)
        {
            return;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (FileInfo file in snapshot.Files
                     .Where(file => !string.Equals(file.FullName, current.FullName, comparison))
                     .OrderBy(static file => file.LastWriteTimeUtc))
        {
            file.Delete();
            filesToDelete--;
            if (filesToDelete == 0)
            {
                return;
            }
        }

        if (current.Exists
            && current.LinkTarget is null
            && !current.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // A one-file policy cannot retain a rotated predecessor. Replace the full current
            // JSONL file; pointer files, bootstrap logs and the build-log directory do not consume
            // the managed JSONL directory-entry cap.
            current.Delete();
            return;
        }

        throw new IOException("Diagnostic logging cannot create a new bounded JSONL file within its managed-file cap.");
    }

    private void EnsureDiagnosticsDirectoryIsPhysical()
    {
        DirectoryInfo directory = new(_paths.DiagnosticsDirectory);
        if (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Diagnostic logging does not write through a symbolic-link or reparse-point directory.");
        }
    }

    private static void EnsureCurrentLogIsPhysical(string path)
    {
        FileInfo current = new(path);
        if (current.Exists && (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new IOException("Diagnostic logging does not write through a symbolic-link or reparse-point file.");
        }
    }

    private sealed record DiagnosticDirectorySnapshot(List<FileInfo> Files);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        bool writerCompleted = WaitWithoutThrowing(_writerTask, GracefulShutdownTimeout);
        if (!writerCompleted)
        {
            _shutdown.Cancel();
            writerCompleted = WaitWithoutThrowing(_writerTask, CancelledShutdownTimeout);
        }

        if (writerCompleted)
        {
            _shutdown.Dispose();
            return;
        }

        _ = _writerTask.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            _shutdown,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool WaitWithoutThrowing(Task task, TimeSpan timeout)
    {
        try
        {
            return task.Wait(timeout);
        }
        catch (AggregateException exception) when (
            exception.Flatten().InnerExceptions.All(static inner => !IsFatalDiagnosticException(inner)))
        {
            // Recoverable diagnostic failure is non-fatal; observing the task keeps shutdown deterministic.
            _ = task.Exception;
            return true;
        }
    }

    private sealed class BoundedJsonLogger(
        string category,
        BoundedJsonFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider.ScopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Enqueue(logLevel, eventId, category, formatter(state, exception), exception);
            }
        }
    }
}
