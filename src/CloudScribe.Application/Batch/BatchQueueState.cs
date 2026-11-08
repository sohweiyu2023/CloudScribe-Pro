namespace CloudScribe.Application.Batch;

public enum BatchQueueState
{
    /// <summary>Files imported and reviewed; nothing has run.</summary>
    Ready = 0,

    /// <summary>A run owns the queue.</summary>
    Running = 1,

    /// <summary>Stopped at a safe boundary (pause, missing authority, failure policy, or interruption).</summary>
    Paused = 2,

    /// <summary>Every included file reached a terminal state.</summary>
    Finished = 3,
}
