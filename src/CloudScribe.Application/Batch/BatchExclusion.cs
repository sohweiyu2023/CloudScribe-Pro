namespace CloudScribe.Application.Batch;

/// <summary>Why a selected file is not narrated. Shown to the user with a plain explanation.</summary>
public enum BatchExclusion
{
    None = 0,
    Empty = 1,
    NotUnicodeText = 2,
    TooLarge = 3,
    Unreadable = 4,
    Duplicate = 5,
    Unsupported = 6,
    RemovedByUser = 7,
}
