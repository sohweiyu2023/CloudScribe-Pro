namespace CloudScribe.Domain.Generation;

/// <summary>Fixed local protocol codes only; never accepts subprocess prose or provider content.</summary>
public enum CompletionProtocolCode
{
    Unknown, DiagnosticScope, DiagnosticManifest, DiagnosticCoverageOrBudget, DiagnosticPolicy, DiagnosticBudget,
    DiagnosticNativePin, DiagnosticParent, DiagnosticAcceptedCoverage, DiagnosticChild, DiagnosticChildExists,
    DiagnosticOwnedClaim, DiagnosticFinancialChanged, DiagnosticHistory, DiagnosticPayload, DiagnosticInstalledPolicy,
    DiagnosticOperation, DiagnosticDispatch, DiagnosticDispatchReplay, DiagnosticCounterOrEpoch, DiagnosticDatabasePin, DiagnosticWindow,
    TransitionFinancialAdmissionExists, ClosedDatabaseRequired, Oversized, DiagnosticUnusedRenewal,
    PreviousGatePin, MutablePreviousGatePath, PredecessorSnapshotChanged, PredecessorSnapshotLocation,
}
