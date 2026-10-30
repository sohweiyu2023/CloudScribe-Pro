namespace CloudScribe.Application.Logging;

public enum LocalRequestReason
{
    Unknown, ReviewMissing, ReviewExpired, ConfirmationMismatch, SelectionChanged, SourceChanged, CompilationChanged,
    AdmissionChanged, FinancialChanged, CaptureUnsaved, CaptureSelectionChanged, CaptureIdentityChanged,
    CaptureConsentWithdrawn, Cancelled, Storage, AccessDenied, MalformedState, ValidationRejected, Unexpected,
}
