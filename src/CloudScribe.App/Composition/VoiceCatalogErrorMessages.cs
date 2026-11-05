using System.Net;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public static class VoiceCatalogErrorMessages
{
    public static string For(Exception exception, GoogleCatalogRefreshPhase? phase = null) =>
        For(exception, phase, restrictedRefresh: false);

    public static string For(Exception exception, GoogleCatalogRefreshPhase? phase, bool restrictedRefresh)
    {
        var reported = GoogleCatalogRefreshDiagnostics.ReportedPhase(exception, phase);
        int status = GoogleCatalogRefreshDiagnostics.HttpStatus(exception);
        return Message(exception, status, restrictedRefresh) + $" Diagnostic: {GoogleCatalogRefreshDiagnostics.Category(exception)}"
            + (reported is null ? string.Empty : $" at {reported}")
            + (exception is CompletionGateException gate && gate.Code != CompletionProtocolCode.Unknown && Enum.IsDefined(gate.Code) ? $"; local code {gate.Code}" : string.Empty)
            + (GoogleCatalogRefreshDiagnostics.OAuthFailure(exception) is var oauth && oauth != GoogleOAuthFailure.None ? $"; OAuth {oauth}" : string.Empty)
            + (status == 0 ? "." : $"; HTTP {status}.");
    }

    private static string Message(Exception exception, int status, bool restrictedRefresh) => restrictedRefresh && exception is not CompletionGateException
        ? "Diagnostic voice verification did not complete. The run may be stopped or awaiting reconciliation. Review its recorded state before another attempt. Existing audio and reservations are retained; no automatic retry was made."
        : GoogleCatalogRefreshDiagnostics.Category(exception) is "saved-credential-unavailable"
        ? "The saved Google connection's local credential material is unavailable in this Windows user context. Check the Windows account and Setups, and keep your original service-account file for recovery. No speech was requested and no key was changed."
        : status switch
        {
            (int)HttpStatusCode.Unauthorized => "Google did not accept the saved connection. Inspect connection status in Setups; do not paste or replace your key just to retry.",
            (int)HttpStatusCode.Forbidden => "Google refused voice access. Check Text-to-Speech API enablement, billing and service-account access in your Google project, then refresh. Your saved key was not changed.",
            _ => exception switch
            {
                CompletionGateException { Code: CompletionProtocolCode.ClosedDatabaseRequired } => "Voice loading stopped because the local verifier expected a closed database while CloudScribe was running. This is a local app verification problem. Keep your existing audio and reservations; refreshing again cannot repair it. Inspect diagnostics for the local code.",
                CompletionGateException { Code: CompletionProtocolCode.Oversized } => "Voice loading stopped because a local verification file exceeded its allowed size. Keep your existing audio and reservations, and inspect local diagnostics before retrying. Your saved Google connection was not changed.",
                CompletionGateException gate => $"The supervised narration run is paused because {CompletionReason(gate.Reason)}. Existing audio and reservations are retained. The run needs review before continuing; no automatic retry was made.",
                OperationCanceledException => "Voice loading stopped or timed out. Check your internet connection, then choose Refresh real Google voices to retry.",
                HttpRequestException => "Could not reach Google to load voices. Check your internet connection and try Refresh real Google voices. No speech was requested.",
                _ => "The voice list could not be verified. Inspect the saved connection in Setups, check the language/search filters, then refresh. No speech was requested and no key was changed.",
            },
        };

    private static string CompletionReason(CompletionGateFailure reason) => reason switch
    {
        CompletionGateFailure.Expired => "its approved time window ended",
        CompletionGateFailure.ClockRollback or CompletionGateFailure.ClockDiscontinuity => "the computer's timing could not be verified",
        CompletionGateFailure.PendingOutcome => "an earlier step still needs verification",
        CompletionGateFailure.Limit => "its approved refresh or review limit was reached",
        CompletionGateFailure.PinMismatch => "its saved approval no longer matches the current app or connection",
        CompletionGateFailure.Storage => "its local completion record could not be read or saved",
        CompletionGateFailure.Stopped => "an earlier step failed or stopped",
        _ => "its local verification did not complete",
    };
}
