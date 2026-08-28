namespace CloudScribe.Application.Generation;

public sealed record NativeMediaToolResult(
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}
