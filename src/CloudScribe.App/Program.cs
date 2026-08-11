using Avalonia;
using CloudScribe.App.Composition;
using CloudScribe.App.Diagnostics;
using CloudScribe.Application.Logging;
using CloudScribe.Application.Startup;
using CloudScribe.Infrastructure.Activation;
using CloudScribe.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.App;

internal static class Program
{
    private static readonly TimeSpan HostDisposeTimeout = TimeSpan.FromSeconds(6);

    [STAThread]
    public static int Main(string[] args)
    {
        BootstrapDiagnosticLog.InstallGlobalExceptionHandlers();
        BootstrapDiagnosticLog.ProcessStarting(args.Length);
        try
        {
            int exitCode = RunAsync(args).GetAwaiter().GetResult();
            BootstrapDiagnosticLog.Write("process.exit", $"exitCode={exitCode}");
            return exitCode;
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            BootstrapDiagnosticLog.Write("process.fatal-startup", "Unhandled startup failure.", exception);
            // Host construction and mandatory option validation occur before a logger can be
            // resolved. Keep that process-boundary failure deterministic without disclosing
            // paths, configuration values, or exception details to an uncontrolled console.
            TryWriteFatalStartupMessage();
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        IHost host = CompositionRoot.BuildHost();
        ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CloudScribe.Startup");
        CloudScribeOptions options = host.Services.GetRequiredService<IOptions<CloudScribeOptions>>().Value;
        AppPaths paths = host.Services.GetRequiredService<AppPaths>();
        TimeProvider timeProvider = host.Services.GetRequiredService<TimeProvider>();
        ISingleInstanceCoordinator coordinator = host.Services.GetRequiredService<ISingleInstanceCoordinator>();
        InitializeDiagnostics(paths, logger);
        bool hostStartAttempted = false;
        using CancellationTokenSource startup = new(
            TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
            timeProvider);
        CloudScribeLog.ApplicationStarting(logger, "offline");
        try
        {
            if (!await coordinator.TryBecomePrimaryAsync(args, startup.Token).ConfigureAwait(false))
            {
                CloudScribeLog.SecondaryActivationForwarded(logger, args.Length);
                return 0;
            }

            CloudScribeLog.PrimaryInstanceAcquired(logger, args.Length);
            hostStartAttempted = true;
            return await StartHostAndApplicationAsync(host, args, startup, logger).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested)
        {
            CloudScribeLog.ApplicationStartupTimedOut(logger, options.StartupTimeoutSeconds);
            return 2;
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            CloudScribeLog.ApplicationStartupFailed(logger, exception);
            return 1;
        }
        finally
        {
            await ShutdownAsync(host, coordinator, hostStartAttempted, timeProvider, logger).ConfigureAwait(false);
        }
    }

    private static void InitializeDiagnostics(AppPaths paths, ILogger logger)
    {
        try
        {
            paths.EnsureDiagnosticsDirectory();
            CloudScribeLog.DiagnosticsDirectorySelected(
                logger,
                paths.DiagnosticsDirectory,
                paths.DiagnosticsLocationMode,
                paths.DiagnosticsFallbackReason ?? "none");
            BootstrapDiagnosticLog.Write(
                "structured-logging.ready",
                $"directory={paths.DiagnosticsDirectory}; mode={paths.DiagnosticsLocationMode}");
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            BootstrapDiagnosticLog.Write(
                "structured-logging.unavailable",
                "Structured diagnostic directory initialization failed; application startup continues.",
                exception);
        }
    }

    private static async Task<int> StartHostAndApplicationAsync(
        IHost host,
        string[] args,
        CancellationTokenSource startup,
        ILogger logger)
    {
        CloudScribeLog.HostStarting(logger);
        await host.StartAsync(startup.Token).ConfigureAwait(false);
        CloudScribeLog.HostStarted(logger);
        foreach (IApplicationInitializer initializer in host.Services.GetServices<IApplicationInitializer>())
        {
            string initializerName = initializer.GetType().FullName ?? initializer.GetType().Name;
            CloudScribeLog.InitializerStarting(logger, initializerName);
            await initializer.InitializeAsync(startup.Token).ConfigureAwait(false);
            CloudScribeLog.InitializerCompleted(logger, initializerName);
        }

        startup.Token.ThrowIfCancellationRequested();
        startup.CancelAfter(Timeout.InfiniteTimeSpan);
        CloudScribeLog.DesktopLifetimeStarting(logger);
        int exitCode = BuildAvaloniaApp(host).StartWithClassicDesktopLifetime(args);
        CloudScribeLog.DesktopLifetimeExited(logger, exitCode);
        return exitCode;
    }

    private static async Task ShutdownAsync(
        IHost host,
        ISingleInstanceCoordinator coordinator,
        bool hostStartAttempted,
        TimeProvider timeProvider,
        ILogger logger)
    {
        CloudScribeLog.ApplicationStopping(logger);
        if (hostStartAttempted)
        {
            using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(5), timeProvider);
            try
            {
                CloudScribeLog.HostStopping(logger);
                await host.StopAsync(shutdown.Token).ConfigureAwait(false);
                CloudScribeLog.HostStopped(logger);
            }
            catch (Exception exception) when (!IsFatalProcessException(exception))
            {
                CloudScribeLog.ApplicationShutdownFailed(logger, exception);
            }
        }

        try
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
            CloudScribeLog.SingleInstanceCoordinatorDisposed(logger);
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            CloudScribeLog.ApplicationShutdownFailed(logger, exception);
        }

        await DisposeHostBoundedAsync(host, timeProvider, logger).ConfigureAwait(false);
    }

    private static async Task DisposeHostBoundedAsync(
        IHost host,
        TimeProvider timeProvider,
        ILogger logger)
    {
        Task disposeTask = Task.Run(host.Dispose, CancellationToken.None);
        try
        {
            await disposeTask.WaitAsync(HostDisposeTimeout, timeProvider).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            // Host disposal is best-effort at the process boundary. The task is observed even
            // after timeout, and a partially disposed logger cannot replace the exit outcome.
            _ = disposeTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                CloudScribeLog.ApplicationShutdownFailed(logger, exception);
            }
            catch (Exception loggingException) when (!IsFatalProcessException(loggingException))
            {
            }
        }
    }

    private static bool IsFatalProcessException(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private static void TryWriteFatalStartupMessage()
    {
        try
        {
            Console.Error.WriteLine("CloudScribe Pro could not initialize. Review the logs folder beside CloudScribe.exe or the local application-data fallback.");
        }
        catch (Exception consoleException) when (!IsFatalProcessException(consoleException))
        {
        }
    }

    public static AppBuilder BuildAvaloniaApp(IHost host) =>
        AppBuilder.Configure(() => new CloudScribeApplication(host))
            .UsePlatformDetect();
}
