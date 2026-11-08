namespace CloudScribe.Application.Batch;

public enum BatchFailurePolicy
{
    /// <summary>Record the failed file and continue with the next one.</summary>
    SkipAndContinue = 0,

    /// <summary>Pause the batch at the first failed or uncertain file.</summary>
    StopBatch = 1,
}
