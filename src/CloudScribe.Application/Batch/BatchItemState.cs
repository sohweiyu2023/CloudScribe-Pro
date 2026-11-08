namespace CloudScribe.Application.Batch;

/// <summary>Lifecycle of one queued file. Completed, Failed, Uncertain, Excluded and Skipped are terminal until a user acts.</summary>
public enum BatchItemState
{
    /// <summary>Imported into the library and waiting to be narrated.</summary>
    Ready = 0,

    /// <summary>Narration is in progress, or was interrupted and must be reconciled before anything new is sent.</summary>
    Running = 1,

    /// <summary>A verified MP3 exists at the recorded path with the recorded hash.</summary>
    Completed = 2,

    /// <summary>Stopped with a known reason. Retry continues the same saved job; it never discards accepted audio.</summary>
    Failed = 3,

    /// <summary>A request may have reached the provider without a recorded outcome. Never resubmitted automatically.</summary>
    Uncertain = 4,

    /// <summary>Not part of this batch (empty, unreadable, duplicate or removed). No request is ever made for it.</summary>
    Excluded = 5,

    /// <summary>Left out of the remaining run by the user.</summary>
    Skipped = 6,
}
