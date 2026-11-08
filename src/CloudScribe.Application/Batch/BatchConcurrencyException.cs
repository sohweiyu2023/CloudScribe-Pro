namespace CloudScribe.Application.Batch;

public sealed class BatchConcurrencyException : InvalidOperationException
{
    public BatchConcurrencyException() : base("This batch was changed elsewhere. Reopen it to see the latest progress.") { }

    public BatchConcurrencyException(string message) : base(message) { }

    public BatchConcurrencyException(string message, Exception innerException) : base(message, innerException) { }
}
