namespace CloudScribe.Application.Batch;

public sealed class BatchSettingsChangedException : InvalidOperationException
{
    public BatchSettingsChangedException() : base("The voice, language or engine changed since this batch was reviewed. Review the batch again before starting.") { }

    public BatchSettingsChangedException(string message) : base(message) { }

    public BatchSettingsChangedException(string message, Exception innerException) : base(message, innerException) { }
}
