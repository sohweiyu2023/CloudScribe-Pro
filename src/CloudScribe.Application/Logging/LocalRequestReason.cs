namespace CloudScribe.Application.Logging;

public enum LocalRequestReason
{
    Unknown, ReviewMissing, ReviewExpired, ConfirmationMismatch, SelectionChanged, SourceChanged, CompilationChanged,
    AdmissionChanged, FinancialChanged, CaptureUnsaved, CaptureSelectionChanged, CaptureIdentityChanged,
    CaptureConsentWithdrawn, Cancelled, Storage, AccessDenied, MalformedState, ValidationRejected, Unexpected,
    CapabilityStale,
    DocumentConflict,
    ReservedRequestIneligible, ReservedIdentityMalformed, ReservedSourceChanged,
    ReservedEnvelopeChanged, ReservedPayloadChanged,
    ReservedRevisionChanged, CaptureCapabilityBindingChanged,
    ResumePinMismatch, ResumeExpired, ResumeBuildMismatch, ResumeProfileMismatch, ResumePolicyMalformed,
    ResumeHistoryChanged, ResumeProviderChanged, ResumeMappingChanged,
}
