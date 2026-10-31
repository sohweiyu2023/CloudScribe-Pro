using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

/// <summary>Fixed local failure categories only. No exception text, contents, paths, credentials or action authority.</summary>
public static partial class LocalRequestDiagnostics
{
    private static readonly object ContextKey = new();
    private sealed record Context(LocalRequestReason Reason, DateTimeOffset? Created, DateTimeOffset? AdmissionExpiry, DateTimeOffset? CapabilityExpiry);

    // Advisory metadata preserves the original exception type and the existing guard/transaction behavior.
    public static T Mark<T>(T error, LocalRequestReason reason, DateTimeOffset? reviewCreatedUtc = null,
        DateTimeOffset? admissionExpiresUtc = null, DateTimeOffset? capabilityExpiresUtc = null) where T : Exception
    {
        try { error.Data[ContextKey] = new Context(Valid(reason), reviewCreatedUtc, admissionExpiresUtc, capabilityExpiresUtc); }
        catch (Exception failure) when (NonFatal(failure)) { /* Preserve original failure if metadata is unavailable. */ }
        return error;
    }

    public static LocalRequestFailure Describe(Exception error, DateTimeOffset now, DateTimeOffset? admissionExpiry = null)
    {
        Context? context = null;
        int remainingDepth = 8;
        for (Exception? at = error; at is not null; at = at.InnerException)
        {
            try { context = at.Data[ContextKey] as Context; }
            catch (Exception failure) when (NonFatal(failure)) { }
            if (context is not null) break;
            if (--remainingDepth == 0) break;
        }
        LocalRequestReason reason = context?.Reason ?? Category(error);
        long age = context?.Created is { } created ? (long)Math.Clamp((now - created).TotalMilliseconds, 0, 86_400_000) : -1;
        DateTimeOffset? expiry = context?.Created is { } start && start <= DateTimeOffset.MaxValue.AddMinutes(-10) ? start.AddMinutes(10) : null;
        return new(reason, age, expiry, context?.AdmissionExpiry ?? admissionExpiry,
            reason is not (LocalRequestReason.Unexpected or LocalRequestReason.ValidationRejected or LocalRequestReason.Unknown)
            && (reason != LocalRequestReason.ReviewExpired || expiry is not null), context?.CapabilityExpiry);
    }

    public static LocalRequestFailure Failure(ILogger? logger, Guid operation, LocalRequestStage stage, Exception error,
        DateTimeOffset now, DateTimeOffset? admissionExpiry = null)
    {
        var details = Describe(error, now, admissionExpiry);
        if (logger is not null) CloudScribeLog.Observe(() => FailureMessage(logger, 1, operation,
            Enum.IsDefined(stage) ? stage : LocalRequestStage.Unknown, details.Reason, details.ReviewAgeMilliseconds,
            details.ReviewExpiresUtc, details.AdmissionExpiresUtc, details.EvidenceComplete, details.CapabilityExpiresUtc));
        return details;
    }

    public static string UserMessage(LocalRequestFailure failure) => failure.Reason switch
    {
        LocalRequestReason.CapabilityStale => "Google capability verification expired. Refresh the voice catalog for the existing account and preserve your selected voice. An existing reservation may require independently authorized revalidation before it can resume. Do not reserve again. This action did not renew spending permission.",
        LocalRequestReason.ReservedRequestIneligible => "The existing reservation is not eligible for private observation. Inspect its saved outcome and admission expiry; do not reserve or send again automatically.",
        LocalRequestReason.ReservedIdentityMalformed or LocalRequestReason.ReservedSourceChanged => "The selected article does not match the immutable reserved request. Preserve the reservation and inspect its identity before continuing.",
        LocalRequestReason.ReservedEnvelopeChanged => "Current verification differs from the reserved request identity. A catalog refresh does not renew the reservation or spending authority. Preserve the hold; independently authorized revalidation is required before resuming.",
        LocalRequestReason.ReservedRevisionChanged => "The compiled request revision differs from the immutable reservation although its payload and other envelope fields match. Preserve the hold; a fresh catalog does not authorize replacing this revision or reserving again.",
        LocalRequestReason.CaptureCapabilityBindingChanged => "The selected voice is not bound to the current verified catalog snapshot. Refresh the selection while preserving your intended voice. An existing reservation still requires exact revalidation; this action grants no spending authority.",
        LocalRequestReason.ReservedPayloadChanged => "Current compiled bytes differ from the reserved request. Private capture was not enabled. Preserve the reservation and inspect the saved source and request settings.",
        LocalRequestReason.ResumeExpired => "The independently admitted resume scope or its catalog evidence expired. The existing reservation and all holds remain retained; no automatic renewal or request is allowed.",
        LocalRequestReason.ResumePinMismatch or LocalRequestReason.ResumeBuildMismatch or LocalRequestReason.ResumeProfileMismatch or LocalRequestReason.ResumePolicyMalformed => "The exact independently reviewed resume pin, build or profile could not be verified. Inspect the safe admission diagnostic; preserve the held request. No authority was renewed.",
        LocalRequestReason.ResumeHistoryChanged or LocalRequestReason.ResumeProviderChanged or LocalRequestReason.ResumeMappingChanged => "Retained history, current provider evidence or the admitted mapping changed. Inspect its safe diagnostic and saved status; no automatic retry, extra reservation or hold release is allowed.",
        LocalRequestReason.ReviewExpired => "The local request review expired after 10 minutes. Review the selected request again and acknowledge it. Admission expiry and earlier holds are unchanged; no audio request was sent.",
        LocalRequestReason.ReviewMissing => "This request review is no longer available. Prepare a fresh review; inspect any saved reservation before reserving again.",
        LocalRequestReason.ConfirmationMismatch => "The displayed request or acknowledgement changed. Review the exact request again; earlier holds remain retained.",
        LocalRequestReason.SelectionChanged or LocalRequestReason.SourceChanged or LocalRequestReason.CompilationChanged => "The saved text, account, voice or price changed after review. Check the selection and prepare a fresh review. No automatic audio request was made.",
        LocalRequestReason.AdmissionChanged => "The independent admission changed. Check its current scope and expiry before preparing a new review. No automatic renewal or retry was made.",
        LocalRequestReason.FinancialChanged => "The retained spending evidence changed after review. Inspect saved reservations and holds before continuing; no automatic retry was made.",
        LocalRequestReason.CaptureUnsaved => "Save the selected document before enabling private capture. No request was sent.",
        LocalRequestReason.CaptureSelectionChanged or LocalRequestReason.CaptureIdentityChanged or LocalRequestReason.CaptureConsentWithdrawn => "Private capture was not enabled because the request or consent changed. Verify the exact saved request and enable capture again only when intended.",
        LocalRequestReason.Cancelled => "The local action was cancelled. Inspect any saved reservation before continuing; no automatic audio retry was made.",
        LocalRequestReason.Storage or LocalRequestReason.AccessDenied => "Local storage could not complete this action. Inspect saved request status before trying again; earlier holds remain retained.",
        _ => "The local action could not finish. Inspect saved request status before trying again; no automatic audio retry was made. Local diagnostics may have more detail.",
    };

    private static LocalRequestReason Valid(LocalRequestReason value) => Enum.IsDefined(value) ? value : LocalRequestReason.Unexpected;
    private static LocalRequestReason Category(Exception error) => error switch
    {
        OperationCanceledException => LocalRequestReason.Cancelled,
        UnauthorizedAccessException => LocalRequestReason.AccessDenied,
        InvalidDataException => LocalRequestReason.ValidationRejected,
        DbException or IOException => LocalRequestReason.Storage,
        JsonException => LocalRequestReason.MalformedState,
        InvalidOperationException or ArgumentException => LocalRequestReason.ValidationRejected,
        _ => LocalRequestReason.Unexpected,
    };
    private static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    [LoggerMessage(6300, LogLevel.Warning, "Local request failure schema {SchemaVersion}; operation {Operation}; stage {Stage}; reason {Reason}; review age ms {ReviewAgeMilliseconds} (-1 unknown); local review expires {ReviewExpiresUtc}; admission expires {AdmissionExpiresUtc}; evidence complete {EvidenceComplete}; capability expires {CapabilityExpiresUtc}. No exception text/content retained; this event grants no retry or spending authority.")]
    private static partial void FailureMessage(ILogger logger, int schemaVersion, Guid operation, LocalRequestStage stage,
        LocalRequestReason reason, long reviewAgeMilliseconds, DateTimeOffset? reviewExpiresUtc,
        DateTimeOffset? admissionExpiresUtc, bool evidenceComplete, DateTimeOffset? capabilityExpiresUtc);
}
