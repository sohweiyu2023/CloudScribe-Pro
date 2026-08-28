using System.Diagnostics;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class BoundedNativeMediaTool : INativeMediaTool
{
    public async Task<NativeMediaToolResult> RunAsync(
        NativeMediaToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        invocation.Validate();
        if (!File.Exists(invocation.ExecutablePath))
            throw new FileNotFoundException("Native media executable not found.", invocation.ExecutablePath);

        Directory.CreateDirectory(invocation.WorkingDirectory);
        using var process = new Process { StartInfo = CreateStartInfo(invocation), EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException("Native media process could not be started.");

        var stdoutTask = ReadBoundedButDrainAsync(process.StandardOutput, invocation.MaximumCapturedOutputCharacters);
        var stderrTask = ReadBoundedButDrainAsync(process.StandardError, invocation.MaximumCapturedOutputCharacters);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(invocation.Timeout);
        var timedOut = await WaitForExitAsync(process, timeoutCts.Token, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new NativeMediaToolResult(
            timedOut ? -1 : process.ExitCode,
            timedOut,
            stdout,
            stderr,
            stopwatch.Elapsed);
    }

    private static ProcessStartInfo CreateStartInfo(NativeMediaToolInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        try
        {
            await process.WaitForExitAsync(timeoutToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> ReadBoundedButDrainAsync(StreamReader reader, int maximumCharacters)
    {
        var buffer = new char[Math.Min(4096, maximumCharacters)];
        var output = new System.Text.StringBuilder(Math.Min(maximumCharacters, 16_384));
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
                break;

            var remaining = maximumCharacters - output.Length;
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(read, remaining));
        }

        return output.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
