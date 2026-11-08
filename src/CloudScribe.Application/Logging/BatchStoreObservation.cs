namespace CloudScribe.Application.Logging;

public enum BatchStoreObservation
{
    MissingOrInaccessible, Empty, Oversized, Unsupported, LoadedBackup, NoReadableQueue, DamagedPrimaryPreserved, Saved,
}
