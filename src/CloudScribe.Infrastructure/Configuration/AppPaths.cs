using System.Security;
using CloudScribe.Infrastructure.Files;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Configuration;

public sealed class AppPaths
{
    private readonly System.Threading.Lock _diagnosticsGate = new();
    private readonly bool _diagnosticsPreferredIsExecutable;
    private string? _resolvedDiagnosticsDirectory;
    private string? _diagnosticsFallbackReason;

    public AppPaths(IOptions<CloudScribeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? configured = options.Value.AppDataDirectoryOverride;
        bool hasConfiguredRoot = !string.IsNullOrWhiteSpace(configured);
        string root;
        if (!hasConfiguredRoot)
        {
            root = GetDefaultRootDirectory();
        }
        else if (!AppDataPathPolicy.TryResolveOverride(configured!, out root, out string failure))
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(CloudScribeOptions),
                [failure]);
        }

        RootDirectory = root;
        ExecutableDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        DatabasePath = Path.Combine(root, "data", "cloudscribe.db");
        _diagnosticsPreferredIsExecutable = !hasConfiguredRoot;
        PreferredDiagnosticsDirectory = hasConfiguredRoot
            ? Path.Combine(root, "logs")
            : Path.Combine(ExecutableDirectory, "logs");
        FallbackDiagnosticsDirectory = Path.Combine(root, "logs");
        SupportBundleStagingDirectory = Path.Combine(root, "support-bundles");
        InstanceLockPath = Path.Combine(root, ".instance.lock");
    }

    public string RootDirectory { get; }

    public string ExecutableDirectory { get; }

    public string DatabasePath { get; }

    public string PreferredDiagnosticsDirectory { get; }

    public string FallbackDiagnosticsDirectory { get; }

    public string DiagnosticsDirectory =>
        Volatile.Read(ref _resolvedDiagnosticsDirectory) ?? PreferredDiagnosticsDirectory;

    public bool DiagnosticsUsingFallback
    {
        get
        {
            string? resolved = Volatile.Read(ref _resolvedDiagnosticsDirectory);
            return resolved is not null && !PathsEqual(resolved, PreferredDiagnosticsDirectory);
        }
    }

    public string DiagnosticsLocationMode => DiagnosticsUsingFallback
        ? "local-app-data-fallback"
        : _diagnosticsPreferredIsExecutable
            ? "executable-folder"
            : "configured-application-data";

    public string? DiagnosticsFallbackReason => Volatile.Read(ref _diagnosticsFallbackReason);

    public string SupportBundleStagingDirectory { get; }

    public string InstanceLockPath { get; }

    public void EnsureRootDirectory() => EnsureOwnedDirectory(RootDirectory);

    public void EnsureDatabaseDirectory() => EnsureOwnedDirectory(Path.GetDirectoryName(DatabasePath)!);

    public void EnsureDiagnosticsDirectory() => _ = ResolveDiagnosticsDirectory();

    public void EnsureSupportBundleStagingDirectory() => EnsureOwnedDirectory(SupportBundleStagingDirectory);

    public void EnsureDirectories()
    {
        EnsureRootDirectory();
        EnsureDatabaseDirectory();
        EnsureDiagnosticsDirectory();
        EnsureSupportBundleStagingDirectory();
    }

    public string ResolveDiagnosticsDirectory()
    {
        string? resolved = Volatile.Read(ref _resolvedDiagnosticsDirectory);
        if (resolved is not null)
        {
            return resolved;
        }

        lock (_diagnosticsGate)
        {
            resolved = _resolvedDiagnosticsDirectory;
            if (resolved is not null)
            {
                return resolved;
            }

            Exception? preferredFailure = null;
            try
            {
                resolved = PrepareWritableDiagnosticsDirectory(PreferredDiagnosticsDirectory);
            }
            catch (Exception exception) when (IsRecoverableDiagnosticsPathException(exception))
            {
                preferredFailure = exception;
            }

            if (resolved is null && !PathsEqual(PreferredDiagnosticsDirectory, FallbackDiagnosticsDirectory))
            {
                try
                {
                    resolved = PrepareWritableDiagnosticsDirectory(FallbackDiagnosticsDirectory);
                    _diagnosticsFallbackReason = preferredFailure?.GetType().Name ?? "preferred-directory-unavailable";
                }
                catch (Exception fallbackFailure) when (IsRecoverableDiagnosticsPathException(fallbackFailure))
                {
                    throw new IOException(
                        "Neither the executable logs directory nor the local application-data fallback is writable.",
                        new AggregateException(preferredFailure!, fallbackFailure));
                }
            }

            if (resolved is null)
            {
                throw new IOException(
                    "The configured CloudScribe logs directory is not writable.",
                    preferredFailure);
            }

            Volatile.Write(ref _resolvedDiagnosticsDirectory, resolved);
            return resolved;
        }
    }

    private static string PrepareWritableDiagnosticsDirectory(string path)
    {
        string directory = EnsureOwnedDirectory(path);
        string probePath = Path.Combine(
            directory,
            $".cloudscribe-write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
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

        return directory;
    }

    private static bool IsRecoverableDiagnosticsPathException(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or SecurityException
        or NotSupportedException
        or ArgumentException;

    private static string EnsureOwnedDirectory(string path) =>
        PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(
            path,
            "CloudScribe application-data and log directories cannot traverse a symbolic link or reparse point.");

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string GetDefaultRootDirectory()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                throw new InvalidOperationException("A per-user application-data directory could not be resolved.");
            }

            localApplicationData = OperatingSystem.IsWindows()
                ? Path.Combine(userProfile, "AppData", "Local")
                : Path.Combine(userProfile, ".local", "share");
        }

        return Path.GetFullPath(Path.Combine(localApplicationData, "CloudScribe Pro"));
    }
}
