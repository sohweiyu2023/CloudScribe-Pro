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
        string replacement = review.ApprovedReplacementCount > 0
            ? $"Separately approved exact range replacements: {review.ApprovedReplacementCount}; additional retained hold up to US${Money(review.AdditionalReplacementHoldCents)}. Each replacement is sent at most once; no replacement chain is automatic. Only a confirmed sentence-length rejection can continue through the ordinary smaller-range recovery. Other failures stop. Verified ranges and all original holds stay preserved.\n\n"
            : string.Empty;
        return $"Article: {review.Title}\nSaved revision: {review.RevisionId:N}\nVoice: {review.Request.VoiceName}; {review.Request.LanguageCode}; {review.Request.AudioEncoding}.\n\n{funding}\n" +
            $"Total provision, including existing grant, prior Long Audio holds and tax/FX allowance: US${Money(review.Cost.ProjectedProvisionUsdCents)} of US${Money(review.Cost.TotalCapUsdCents)}. This is an estimate/provision, not a final invoice; Google storage and request charges also count toward the cap.\n\n" +
            (review.DeferredUnknownRanges is { Count: > 0 }
                ? review.DeferredUnknownRanges.Any(x => x.ExplicitRegeneration is not null)
                    ? "The original uncertain request and its possible charge remain preserved. One separately approved new request may regenerate that exact section; both requests count toward the cap. The final MP3 uses verified replacement coverage once and does not claim the original outcome is resolved. Any new uncertainty stops; no further unchanged retry is automatic.\n\n"
                    : "An explicitly reviewed unknown source range and its spending hold remain preserved. Continue only other, disjoint ranges of this article; the unknown request will not be repeated. The final MP3 stays unavailable until its gap has verified coverage. Any new uncertain outcome stops this continuation.\n\n" : string.Empty) +
            replacement + recovery + (review.ApprovedReplacementCount > 0 ? "No other unchanged paid retry or voice substitution. " : "No unchanged paid retry or voice substitution. ") +
            "Original WAVs are preserved; newly assembled article MP3s use fixed 1.5 dB attenuation to reduce clipping risk, without normalization, tempo or pitch changes. Previously completed files remain unchanged." +
            (review.HistoricalRevision ? "\nThis resumes the original saved revision; the current editor contains a newer revision." : string.Empty);
    }

    private static string LongAudioStartText(GoogleLongAudioDocumentReview review) => review.DeferredUnknownRanges?.Any(x => x.ExplicitRegeneration is null) == true
        ? "Continue other article ranges" : review.AdaptiveRecoveryEnabled
        ? review.Cost.IsAlreadyCommitted ? "Resume complete article MP3" : "Generate complete article MP3"
        : review.Cost.IsAlreadyCommitted ? "Resume saved Long Audio job" : "Generate Long Audio " + (string.Equals(review.Request.AudioEncoding, "LINEAR16", StringComparison.Ordinal) ? "WAV" : "MP3");

    private static string LongAudioCompletedReviewText(GoogleLongAudioDocumentReview review) =>
        $"Article: {review.Title}\nSaved revision: {review.RevisionId:N}\nVoice: {review.Request.VoiceName}; {review.Request.LanguageCode}.\n" +
        $"Retained provision, including prior usage and tax/FX allowance: US${(review.Cost.ProjectedProvisionUsdCents / 100m).ToString("0.00", CultureInfo.InvariantCulture)}. This is not a final invoice. Original WAVs and spending history are preserved." +
        (review.DeferredUnknownRanges is { Count: > 0 } ? "\n" + LongAudioDialogPresentation.CompletedRequestNotice(true) : string.Empty);

    private static string LongAudioProgressText(GoogleLongAudioDocumentProgress progress, bool hasDeferredUnknown = false)
    {
        string text = progress.Stage switch
        {
            LongAudioWorkflowStage.LocalSetup => "Checking your saved document, Google connection, audio tools and spending records.",
            LongAudioWorkflowStage.SubmittingOrResuming => "Sending this section to Google, or picking up a request that was already sent. Each request is sent only once.",
            LongAudioWorkflowStage.WaitingForGoogle => "Google is creating the speech. CloudScribe checks every few seconds. Google does not report a percentage, so the bar shows how much of your text is already saved as audio.",
            LongAudioWorkflowStage.CheckingGoogleAndOutput => "Downloading the finished audio privately, then checking it is complete and plays correctly.",
            LongAudioWorkflowStage.JoiningArticle => "All sections are saved. Joining them in order into one MP3 on this computer. No more Google requests are needed.",
            LongAudioWorkflowStage.VerifyingArticle => "Checking the finished MP3 from start to end before enabling Play and Save.",
            LongAudioWorkflowStage.Completed => "Your MP3 is ready. Play it, save a copy or open its folder below. Nothing needs to be generated again.",
            LongAudioWorkflowStage.Stopped => LongAudioDialogPresentation.StoppedGuidance,
            LongAudioWorkflowStage.DeferredGap => "One section's request never got an answer from Google, so it is kept aside and not re-sent. The other sections are saved, but the full MP3 needs that section first.",
            _ => "Long Audio stopped. " + LongAudioFailureAdvice(progress.Checkpoint?.FailureKind) + " Keep this job; do not start a duplicate." +
                $"\nTechnical details: state {progress.Checkpoint?.State}; HTTP {progress.Checkpoint?.HttpStatus}; RPC {progress.Checkpoint?.RpcCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}; " +
                $"private response saved {progress.Checkpoint?.Observation?.ReadbackVerified}, truncated {progress.Checkpoint?.Observation?.Truncated}, details omitted {progress.Checkpoint?.Observation?.DetailsOmitted}."
        };
        if (progress.Stage == LongAudioWorkflowStage.Completed && hasDeferredUnknown)
            text += "\n" + LongAudioDialogPresentation.CompletedRequestNotice(true);
        return progress.VerifiedScalars is { } saved
            ? text + $"\nSaved so far: {saved:N0} of {progress.TotalScalars:N0} characters, in {progress.VerifiedRanges:N0} verified sections."
            : text;
    }

    private static string LongAudioFailureAdvice(GoogleLongAudioFailureKind? kind) => kind switch
    {
        GoogleLongAudioFailureKind.SentenceLengthReported => "Google said some sentences were too long. CloudScribe splits that section at existing punctuation and sends the smaller parts; the rejected text is never re-sent as it was.",
        GoogleLongAudioFailureKind.AuthenticationOrPermission => "Google reported an account or permission problem. Check the Google connection and the storage bucket permissions; splitting the text will not help.",
        GoogleLongAudioFailureKind.QuotaOrRateLimited => "Google reported a usage limit (quota). Wait and try again later, or raise the quota in Google Cloud; splitting the text will not help.",
        GoogleLongAudioFailureKind.TransientOrTimeout => "Google reported a temporary service error. The saved request is kept and never sent twice by accident.",
        GoogleLongAudioFailureKind.OtherInputRejected => "Google rejected a setting for this request (not sentence length), so automatic splitting does not apply.",
        _ => "Google did not give a recognisable reason, so no automatic recovery was attempted."
    };
}
