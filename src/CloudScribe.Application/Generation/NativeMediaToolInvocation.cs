namespace CloudScribe.Application.Generation;

public sealed record NativeMediaToolInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaximumCapturedOutputCharacters)
{
    public void Validate() => ValidateFields(
        ExecutablePath,
        Arguments,
        WorkingDirectory,
        Timeout,
        MaximumCapturedOutputCharacters);

    private static void ValidateFields(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maximumCapturedOutputCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("Native media executable path must be fully qualified.", nameof(executablePath));
        }

        if (!Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("Native media working directory must be fully qualified.", nameof(workingDirectory));
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Native media timeout must be positive and bounded to 30 minutes.");
        }

        if (maximumCapturedOutputCharacters is < 1024 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCapturedOutputCharacters));
        }

        if (arguments.Any(argument => argument is null))
        {
            throw new ArgumentException("Native media arguments cannot contain null values.", nameof(arguments));
        }
    }
}
