using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Activation;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Activation;

public sealed class SingleInstanceCoordinator(
    AppPaths paths,
    IActivationRouter activationRouter,
    TimeProvider timeProvider,
    ILogger<SingleInstanceCoordinator> logger) : ISingleInstanceCoordinator
{
    private const int MaximumActivationArguments = 64;
    private const int MaximumActivationBytes = 8192;
    // A secondary connection must remain pending longer than one stalled client's bounded
    // read window, otherwise equal deadlines race and a healthy activation can fail just as
    // the primary listener becomes available again.
    private static readonly TimeSpan ActivationConnectionTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ActivationReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ListenerRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ListenerShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _listenerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileStream? _lockStream;
    private Task? _listenerTask;
    private int _disposed;
    private int _started;

    public async Task<bool> TryBecomePrimaryAsync(
        IReadOnlyList<string> activationArguments,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(activationArguments);
        ValidateActivationArguments(activationArguments);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Single-instance coordination has already started.");
        }

        paths.EnsureRootDirectory();
        IOException? lockException = TryAcquireInstanceLock();
        if (lockException is not null)
        {
            return await RouteToPrimaryInstanceAsync(
                    activationArguments,
                    lockException,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await StartPrimaryInstanceAsync(activationArguments, cancellationToken).ConfigureAwait(false);
    }

    private IOException? TryAcquireInstanceLock()
    {
        try
        {
            // Holding an exclusive, open file handle provides a cross-platform process-lifetime lock.
            // The operating system releases it after a crash, so no stale lock-file cleanup is required.
            _lockStream = new FileStream(
                paths.InstanceLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return null;
        }
        catch (IOException exception)
        {
            return exception;
        }
    }

    private async Task<bool> RouteToPrimaryInstanceAsync(
        IReadOnlyList<string> activationArguments,
        IOException lockException,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendActivationAsync(activationArguments, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception activationException) when (
            activationException is IOException or TimeoutException
            || activationException is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                "CloudScribe could neither acquire the single-instance lock nor contact the primary instance.",
                new AggregateException(lockException, activationException));
        }
    }

    private async Task<bool> StartPrimaryInstanceAsync(
        IReadOnlyList<string> activationArguments,
        CancellationToken cancellationToken)
    {
        _listenerTask = Task.Run(() => ListenAsync(_shutdown.Token), CancellationToken.None);
        try
        {
            await WaitForListenerReadyAsync(cancellationToken).ConfigureAwait(false);
            RoutePrimaryLaunch(activationArguments);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbortPrimaryStartup();
            throw;
        }
        catch (Exception exception) when (!IsFatalActivationException(exception))
        {
            AbortPrimaryStartup();
            throw new IOException(
                "CloudScribe acquired the instance lock but could not initialize activation routing.",
                exception);
        }
    }

    private void RoutePrimaryLaunch(IReadOnlyList<string> activationArguments)
    {
        if (activationArguments.Count == 0)
        {
            return;
        }

        activationRouter.Route(new ActivationReceivedEventArgs(
            ActivationSource.PrimaryLaunch,
            activationArguments,
            timeProvider.GetUtcNow()));
        CloudScribeLog.ActivationReceived(
            logger,
            ActivationSource.PrimaryLaunch,
            activationArguments.Count);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessActivationConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (TimeoutException)
            {
                // A connected client that does not complete its bounded line cannot monopolize the listener.
            }
            catch (IOException)
            {
                await Task.Delay(ListenerRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Malformed secondary activation is ignored; it cannot affect durable state.
            }
            catch (DecoderFallbackException)
            {
                // Invalid UTF-8 activation input is ignored.
            }
        }
    }

    private async Task ProcessActivationConnectionAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream server = new(
            PipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _listenerReady.TrySetResult();
        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            string? line = await ReadBoundedUtf8LineAsync(server, cancellationToken)
                .WaitAsync(ActivationReadTimeout, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            if (line is null || !TryParseActivationArguments(line, out string[] arguments))
            {
                return;
            }

            RouteSecondaryActivation(arguments);
        }
    }

    private static bool TryParseActivationArguments(string payload, out string[] arguments)
    {
        string?[]? nullableArguments = JsonSerializer.Deserialize<string?[]>(payload);
        if (nullableArguments is null
            || nullableArguments.Length > MaximumActivationArguments
            || nullableArguments.Any(static argument => argument is null))
        {
            arguments = Array.Empty<string>();
            return false;
        }

        arguments = nullableArguments.Select(static argument => argument!).ToArray();
        return true;
    }

    private void RouteSecondaryActivation(string[] arguments)
    {
        try
        {
            activationRouter.Route(new ActivationReceivedEventArgs(
                ActivationSource.SecondaryInstance,
                arguments,
                timeProvider.GetUtcNow()));
            CloudScribeLog.ActivationReceived(
                logger,
                ActivationSource.SecondaryInstance,
                arguments.Length);
        }
        catch (Exception exception) when (!IsFatalActivationException(exception))
        {
            CloudScribeLog.ActivationDispatchFailed(logger, exception);
        }
    }

    private static bool IsFatalActivationException(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private void AbortPrimaryStartup()
    {
        _shutdown.Cancel();
        _lockStream?.Dispose();
        _lockStream = null;
    }

    private async Task WaitForListenerReadyAsync(CancellationToken cancellationToken)
    {
        Task listenerTask = _listenerTask
            ?? throw new InvalidOperationException("The activation listener was not started.");
        Task completed = await Task.WhenAny(_listenerReady.Task, listenerTask)
            .WaitAsync(ActivationConnectionTimeout, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        if (completed == listenerTask)
        {
            await listenerTask.ConfigureAwait(false);
            throw new IOException("The activation listener stopped before reporting readiness.");
        }

        await _listenerReady.Task.ConfigureAwait(false);
        if (listenerTask.IsCompleted)
        {
            await listenerTask.ConfigureAwait(false);
        }
    }

    private async Task SendActivationAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string payload = ValidateActivationArguments(arguments);

        NamedPipeClientStream client = new(
            ".",
            PipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client.ConfigureAwait(false))
        {
            using CancellationTokenSource deadline = new(ActivationConnectionTimeout, timeProvider);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using StreamWriter writer = new(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync(payload.AsMemory(), timeout.Token).ConfigureAwait(false);
        }
    }


    private static string ValidateActivationArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > MaximumActivationArguments)
        {
            throw new ArgumentException(
                $"Activation contains {arguments.Count} arguments; the maximum is {MaximumActivationArguments}.",
                nameof(arguments));
        }

        if (arguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("Activation arguments cannot contain null values.", nameof(arguments));
        }

        string payload = JsonSerializer.Serialize(arguments);
        int payloadBytes = Encoding.UTF8.GetByteCount(payload);
        if (payloadBytes > MaximumActivationBytes)
        {
            throw new ArgumentException(
                $"Activation payload is {payloadBytes} UTF-8 bytes; the maximum is {MaximumActivationBytes}.",
                nameof(arguments));
        }

        return payload;
    }

    private static async Task<string?> ReadBoundedUtf8LineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaximumActivationBytes + 1];
        int count = 0;
        while (count <= MaximumActivationBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return count == 0 ? null : StrictUtf8.GetString(buffer, 0, count);
            }

            if (buffer[count] == (byte)'\n')
            {
                int length = count > 0 && buffer[count - 1] == (byte)'\r' ? count - 1 : count;
                return StrictUtf8.GetString(buffer, 0, length);
            }

            count += read;
        }

        return null;
    }

    private string PipeName
    {
        get
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(paths.RootDirectory)));
            return "cloudscribe-pro-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        }
    }

    public void Dispose()
    {
        if (!TryBeginDispose())
        {
            return;
        }

        _shutdown.Cancel();
        CompleteDispose(WaitForListenerSynchronously());
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose())
        {
            return;
        }

        _shutdown.Cancel();
        bool listenerCompleted = _listenerTask is null;
        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.WaitAsync(ListenerShutdownTimeout, timeProvider).ConfigureAwait(false);
                listenerCompleted = true;
            }
            catch (OperationCanceledException)
            {
                listenerCompleted = true;
            }
            catch (TimeoutException)
            {
                listenerCompleted = false;
            }
            catch (Exception exception) when (!IsFatalActivationException(exception))
            {
                // A completed, faulted listener no longer owns pipe resources. Observe the
                // failure and still release the process-lifetime instance lock and shutdown token.
                listenerCompleted = true;
            }
        }

        CompleteDispose(listenerCompleted);
    }

    private bool TryBeginDispose() => Interlocked.Exchange(ref _disposed, 1) == 0;

    private bool WaitForListenerSynchronously()
    {
        if (_listenerTask is null)
        {
            return true;
        }

        try
        {
            return _listenerTask.Wait(ListenerShutdownTimeout);
        }
        catch (AggregateException exception) when (
            exception.Flatten().InnerExceptions.All(static inner => !IsFatalActivationException(inner)))
        {
            // The listener has completed, albeit faulted. Observe the exception and allow
            // deterministic cleanup of the lock handle and cancellation source.
            _ = _listenerTask.Exception;
            return true;
        }
    }

    private void CompleteDispose(bool listenerCompleted)
    {
        _lockStream?.Dispose();
        _lockStream = null;
        if (listenerCompleted)
        {
            _shutdown.Dispose();
            return;
        }

        _ = _listenerTask!.ContinueWith(
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
}
