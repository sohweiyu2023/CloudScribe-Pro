using System.Globalization;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static string LongAudioSetupText(GoogleLongAudioInstallationRead read) => read.Status switch
    {
        GoogleLongAudioInstallationStatus.PendingProvisioning => "Long Audio setup is unfinished or busy. Resume or verify its existing installer transaction; do not create another budget ledger.",
        GoogleLongAudioInstallationStatus.IdentityMismatch => "This app/profile differs from the Long Audio setup. Verify the installation and rebind the reviewed scope; saved audio and spending remain preserved.",
        GoogleLongAudioInstallationStatus.LedgerUnavailable => "Long Audio spending history is missing or mismatched. Restore and reconcile the original ledger before generating; do not start an empty one.",
        _ => "Long Audio configuration changed or could not be read. Verify its reviewed setup before generating. Ordinary article generation is separate."
    };
    private static string LongAudioReviewText(GoogleLongAudioDocumentReview review)
    {
        string Money(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        string funding = review.Cost.IsAlreadyCommitted
            ? review.AdaptiveRecoveryEnabled
                ? "The original request already has a retained commitment. Resume reuses verified audio and never repeats an unknown POST; genuinely new smaller ranges may add holds within the cumulative cap."
                : "This exact job already has a retained commitment. Resume adds no new synthesis hold and never repeats an unknown POST."
            : $"Estimated synthesis US${Money(review.Cost.EstimatedSynthesisUsdCents)}; additional approved hold up to US${Money(review.Cost.AdditionalCommitmentUsdCents)}.";
        string recovery = review.AdaptiveRecoveryEnabled
            ? $"Long Audio first. Only a confirmed sentence-length rejection triggers smaller unchanged ranges, without another confirmation. Each new range retains its own hold; prior failed holds remain counted. Automatic recovery may use the remaining cumulative provision up to US${Money(review.Cost.TotalCapUsdCents)}, including prior usage and the tax/FX allowance. It stops for other errors, uncertain outcomes, indivisible text or exhausted funding.\n\n"
            : "One durable synthesis request is followed by internal status checks; this scope does not authorize automatic split recovery.\n\n";
        return $"Article: {review.Title}\nSaved revision: {review.RevisionId:N}\nVoice: {review.Request.VoiceName}; {review.Request.LanguageCode}; {review.Request.AudioEncoding}.\n\n{funding}\n" +
            $"Total provision, including existing grant, prior Long Audio holds and tax/FX allowance: US${Money(review.Cost.ProjectedProvisionUsdCents)} of US${Money(review.Cost.TotalCapUsdCents)}. This is an estimate/provision, not a final invoice; Google storage and request charges also count toward the cap.\n\n" +
            recovery + "No unchanged paid retry or voice substitution. Original WAVs are preserved; the final article MP3 is encoded once without gain, tempo or pitch filters." +
            (review.HistoricalRevision ? "\nThis resumes the original saved revision; the current editor contains a newer revision." : string.Empty);
    }

    private static string LongAudioStartText(GoogleLongAudioDocumentReview review) => review.AdaptiveRecoveryEnabled
        ? review.Cost.IsAlreadyCommitted ? "Resume complete article MP3" : "Generate complete article MP3"
        : review.Cost.IsAlreadyCommitted ? "Resume saved Long Audio job" : "Generate Long Audio " + (string.Equals(review.Request.AudioEncoding, "LINEAR16", StringComparison.Ordinal) ? "WAV" : "MP3");

    private static string LongAudioCompletedReviewText(GoogleLongAudioDocumentReview review) =>
        $"Article: {review.Title}\nSaved revision: {review.RevisionId:N}\nVoice: {review.Request.VoiceName}; {review.Request.LanguageCode}.\n" +
        $"Retained provision, including prior usage and tax/FX allowance: US${(review.Cost.ProjectedProvisionUsdCents / 100m).ToString("0.00", CultureInfo.InvariantCulture)}. This is not a final invoice. Original WAVs and spending history are preserved.";

    private static string LongAudioProgressText(GoogleLongAudioDocumentProgress progress)
    {
        string text = progress.Stage switch
        {
            LongAudioWorkflowStage.LocalSetup => "Checking the saved source, existing account, tools, capture and spending history.",
            LongAudioWorkflowStage.SubmittingOrResuming => "Submitting once or resuming the exact saved operation. Waiting for the current outcome; do not create another job.",
            LongAudioWorkflowStage.WaitingForGoogle => "Google is processing the saved operation. Status checks continue automatically; there is no separate Long Audio run timer.",
            LongAudioWorkflowStage.CheckingGoogleAndOutput => "Checking Google's operation. Completed audio is downloaded privately, checksum-checked and fully decoded before playback is enabled.",
            LongAudioWorkflowStage.JoiningArticle => "All source ranges are verified. Joining ordered PCM audio and encoding one final MP3 locally; no synthesis request is made.",
            LongAudioWorkflowStage.VerifyingArticle => "Verifying the complete article output and its durable receipt before enabling playback and export.",
            LongAudioWorkflowStage.Completed => "Complete saved audio verified. Play it, save a copy or open its folder below. No regeneration is needed.",
            LongAudioWorkflowStage.Stopped => "Stopped after the current recorded outcome. The operation and spending history are saved; reopen this document to resume status checks.",
            _ => $"Long Audio stopped. Operation state: {progress.Checkpoint?.State}; HTTP: {progress.Checkpoint?.HttpStatus}; RPC: {progress.Checkpoint?.RpcCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}. " +
                $"Private response readback: {progress.Checkpoint?.Observation?.ReadbackVerified}; truncated: {progress.Checkpoint?.Observation?.Truncated}; omitted: {progress.Checkpoint?.Observation?.DetailsOmitted}. " +
                LongAudioFailureAdvice(progress.Checkpoint?.FailureKind) + " Inspect private provider details. Preserve this job; do not submit a duplicate."
        };
        return progress.VerifiedScalars is { } saved
            ? text + $"\nSaved source coverage: {saved} of {progress.TotalScalars} characters across {progress.VerifiedRanges} verified ranges."
            : text;
    }

    private static string LongAudioFailureAdvice(GoogleLongAudioFailureKind? kind) => kind switch
    {
        GoogleLongAudioFailureKind.SentenceLengthReported => "Google explicitly reported sentence length. Recovery must use smaller unchanged source ranges; it must not resend this failed input.",
        GoogleLongAudioFailureKind.AuthenticationOrPermission => "Google reported an account or permission problem. Splitting the text will not correct it.",
        GoogleLongAudioFailureKind.QuotaOrRateLimited => "Google reported a quota or rate limit. Splitting the text is not the recovery action.",
        GoogleLongAudioFailureKind.TransientOrTimeout => "A timeout or transient service error was reported. Reconcile the saved operation; never repeat an uncertain synthesis POST.",
        GoogleLongAudioFailureKind.OtherInputRejected => "Google rejected another input setting. The sentence-splitting path is not enabled for this explanation.",
        _ => "The precise failure category is unavailable; no sentence-length cause is inferred."
    };
}
