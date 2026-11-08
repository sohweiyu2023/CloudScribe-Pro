namespace CloudScribe.Application.Batch;

public sealed class BatchAlreadyRunningException : InvalidOperationException
{
    public BatchAlreadyRunningException() : base("This batch is already running in another window or process.") { }

    public BatchAlreadyRunningException(string message) : base(message) { }

    public BatchAlreadyRunningException(string message, Exception innerException) : base(message, innerException) { }
}
