namespace CloudScribe.Domain.Generation;

/// <summary>Evidence completeness only; never retry, capture consent, or billing authority.</summary>
public enum GoogleExplanationRetentionState
{
    NoActivePrivateTicket,
    PrivateWritePending,
    SavedPrivately,
    IneligibleOrUnparseable,
    ExpiredOrChanged,
    StorageBusy,
    StorageFailed,
    PreparationFailed,
    ExpiredBeforeSave,
}
