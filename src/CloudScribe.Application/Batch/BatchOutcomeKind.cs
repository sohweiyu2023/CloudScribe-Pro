namespace CloudScribe.Application.Batch;

public enum BatchOutcomeKind
{
    /// <summary>The MP3 was written and verified.</summary>
    Completed = 0,

    /// <summary>Stopped with a known reason; nothing is pending at the provider.</summary>
    Failed = 1,

    /// <summary>A request may have reached the provider without a recorded outcome.</summary>
    Uncertain = 2,

    /// <summary>Stopped at a safe boundary because the user paused.</summary>
    Paused = 3,

    /// <summary>No spending authority covers this file. Nothing was sent.</summary>
    NotAuthorized = 4,
}
