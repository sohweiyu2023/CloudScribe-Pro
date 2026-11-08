namespace CloudScribe.Application.Batch;

public sealed class BatchNotAuthorizedException : InvalidOperationException
{
    public BatchNotAuthorizedException() : base("No batch spending authority covers this batch. Nothing was sent to Google.") { }

    public BatchNotAuthorizedException(string message) : base(message) { }

    public BatchNotAuthorizedException(string message, Exception innerException) : base(message, innerException) { }
}
