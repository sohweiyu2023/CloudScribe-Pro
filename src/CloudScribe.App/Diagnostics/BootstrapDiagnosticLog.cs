using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.App.Diagnostics;

internal static class BootstrapDiagnosticLog
{
    private const long MaximumFileBytes = 1024L * 1024L;
    private const long MaximumDirectoryBytes = 8L * 1024L * 1024L;
    private const int MaximumFileCount = 8;
    private static readonly System.Threading.Lock Gate = new();
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static string? _activeDirectory;
    private static int _globalHandlersInstalled;

    public static void InstallGlobalExceptionHandlers()
    {
        if (Interlocked.Exchange(ref _globalHandlersInstalled, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static void ProcessStarting(int argumentCount)
    {
        string version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        Write(
            "process.start",
            $"version={version}; arguments={argumentCount}; os={RuntimeInformation.OSDescription}; architecture={RuntimeInformation.ProcessArchitecture}");
    }

    public static void Write(string eventName, string message, Exception? exception = null)
    {
        try
        {
            string directory = ResolveDirectory();
            lock (Gate)
            {
                WriteCore(directory, eventName, message, exception);
            }
        }
        catch (Exception writeException) when (!IsFatal(writeException))
        {
            // Bootstrap diagnostics are best effort and must never replace the application outcome.
        }
    }


    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        Exception? exception = eventArgs.ExceptionObject as Exception;
        string exceptionType = eventArgs.ExceptionObject?.GetType().FullName ?? "unknown";
        TryWriteDuringUnhandledFailure(
            "process.unhandled-exception",
            $"terminating={eventArgs.IsTerminating}; exceptionType={exceptionType}",
            exception);
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs eventArgs) =>
        Write(
            "task.unobserved-exception",
            $"innerExceptionCount={eventArgs.Exception.Flatten().InnerExceptions.Count}",
            eventArgs.Exception);

    private static void TryWriteDuringUnhandledFailure(
        string eventName,
        string message,
        Exception? exception)
    {
        string? directory = Volatile.Read(ref _activeDirectory);
        if (directory is null || !Gate.TryEnter(100))
        {
            return;
        }

        try
        {
            WriteCore(directory, eventName, message, exception);
        }
        catch (Exception writeException) when (!IsFatal(writeException))
        {
        }
        finally
        {
            Gate.Exit();
        }
    }

    private static void WriteCore(
        string directory,
        string eventName,
        string message,
        Exception? exception)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string path = Path.Combine(directory, $"cloudscribe-bootstrap-{now:yyyyMMdd}.log");
        EnsureRegularOrMissing(path, "Bootstrap logging does not write through a symbolic-link or reparse-point file.");
        string safeEvent = DiagnosticRedactor.Sanitize(eventName);
        string safeMessage = DiagnosticRedactor.Sanitize(message);
        string safeException = exception is null
            ? string.Empty
            : DiagnosticRedactor.Sanitize(exception.ToString());
        string line = string.IsNullOrEmpty(safeException)
            ? $"{now:O}\t{safeEvent}\tpid={Environment.ProcessId}\t{safeMessage}{Environment.NewLine}"
            : $"{now:O}\t{safeEvent}\tpid={Environment.ProcessId}\t{safeMessage}\texception={safeException}{Environment.NewLine}";
        RotateIfRequired(path, now, Utf8WithoutBom.GetByteCount(line));
        File.AppendAllText(path, line, Utf8WithoutBom);
        EnforceDirectoryBounds(directory);
        WriteLatestPointer(directory, Path.GetFileName(path));
    }

    private static void RotateIfRequired(string path, DateTimeOffset now, int incomingBytes)
    {
        if (incomingBytes > MaximumFileBytes)
        {
            throw new IOException("A single bootstrap diagnostic record exceeds the bounded file cap.");
        }

        if (!File.Exists(path) || new FileInfo(path).Length <= MaximumFileBytes - incomingBytes)
        {
            return;
        }

        string rotated = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"cloudscribe-bootstrap-{now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.log");
        File.Move(path, rotated, overwrite: false);
    }


    private static void EnforceDirectoryBounds(string directory)
    {
        FileInfo[] files = new DirectoryInfo(directory)
            .EnumerateFiles("cloudscribe-bootstrap-*.log", SearchOption.TopDirectoryOnly)
            .Where(static file => file.LinkTarget is null
                && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ToArray();
        long total = files.Sum(static file => file.Length);
        for (int index = files.Length - 1;
             index >= 0 && (index >= MaximumFileCount || total > MaximumDirectoryBytes);
             index--)
        {
            FileInfo file = files[index];
            long length = file.Length;
            file.Delete();
            total -= length;
        }
    }

    private static void WriteLatestPointer(string directory, string fileName)
    {
        string pointerPath = Path.Combine(directory, "LATEST-BOOTSTRAP-LOG.txt");
        string stagingPath = pointerPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            EnsureRegularOrMissing(pointerPath, "The bootstrap latest-log pointer cannot be a symbolic link or reparse point.");
            using (FileStream stream = new(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 256,
                       FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream, Utf8WithoutBom, bufferSize: 256, leaveOpen: false))
            {
                writer.WriteLine(fileName);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(stagingPath, pointerPath, overwrite: true);
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


    private static void EnsureRegularOrMissing(string path, string rejectionMessage)
    {
        FileInfo file = new(path);
        if (file.Exists
            && (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new IOException(rejectionMessage);
        }
    }

    private static string ResolveDirectory()
    {
        string? active = Volatile.Read(ref _activeDirectory);
        if (active is not null)
        {
            return active;
        }

        lock (Gate)
        {
            active = _activeDirectory;
            if (active is not null)
            {
                return active;
            }

            string preferred = Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "logs");
            string fallback = Path.Combine(GetDefaultRootDirectory(), "logs");
            Exception? preferredFailure = null;
            try
            {
                active = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(
                    preferred,
                    "Bootstrap logs cannot traverse a symbolic link or reparse point.");
                ProbeWritable(active);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                preferredFailure = exception;
                active = null;
            }

            if (active is null)
            {
                try
                {
                    active = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(
                        fallback,
                        "Bootstrap fallback logs cannot traverse a symbolic link or reparse point.");
                    ProbeWritable(active);
                }
                catch (Exception fallbackFailure) when (IsRecoverable(fallbackFailure))
                {
                    throw new IOException(
                        "Bootstrap diagnostics could not initialize a writable directory.",
                        new AggregateException(preferredFailure!, fallbackFailure));
                }
            }

            Volatile.Write(ref _activeDirectory, active);
            return active;
        }
    }

    private static void ProbeWritable(string directory)
    {
        string probePath = Path.Combine(
            directory,
            $".cloudscribe-bootstrap-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using FileStream probe = new(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            probe.WriteByte(0);
            probe.Flush(flushToDisk: true);
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string GetDefaultRootDirectory()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                throw new InvalidOperationException("A per-user bootstrap log directory could not be resolved.");
            }

            localApplicationData = OperatingSystem.IsWindows()
                ? Path.Combine(userProfile, "AppData", "Local")
                : Path.Combine(userProfile, ".local", "share");
        }

        return Path.GetFullPath(Path.Combine(localApplicationData, "CloudScribe Pro"));
    }

    private static bool IsRecoverable(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or System.Security.SecurityException
        or NotSupportedException
        or ArgumentException;

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;
}
