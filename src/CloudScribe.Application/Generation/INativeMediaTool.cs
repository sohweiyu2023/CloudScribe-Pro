namespace CloudScribe.Application.Generation;

public sealed record NativeMediaToolInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaximumCapturedOutputCharacters)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutablePath);
        ArgumentNullException.ThrowIfNull(Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkingDirectory);
        if (!Path.IsPathFullyQualified(ExecutablePath))
        {
            throw new ArgumentException("Native media executable path must be fully qualified.", nameof(ExecutablePath));
        }

        if (!Path.IsPathFullyQualified(WorkingDirectory))
        {
            throw new ArgumentException("Native media working directory must be fully qualified.", nameof(WorkingDirectory));
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Native media timeout must be positive and bounded to 30 minutes.");
        }

        if (MaximumCapturedOutputCharacters is < 1024 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCapturedOutputCharacters));
        }

        if (Arguments.Any(argument => argument is null))
        {
            throw new ArgumentException("Native media arguments cannot contain null values.", nameof(Arguments));
        }
    }
}

public sealed record NativeMediaToolResult(
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public interface INativeMediaTool
{
    Task<NativeMediaToolResult> RunAsync(
        NativeMediaToolInvocation invocation,
        CancellationToken cancellationToken = default);
}
