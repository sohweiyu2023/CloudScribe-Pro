using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public static class WholeArticleReviewPresentation
{
    public static string ActionLabel(ChirpWholeArticleReview review) => review.BlockedOrdinaryRequests > 0
        ? "Recover already-started work only" : "Generate complete article MP3";

    public static string Estimate(ChirpWholeArticleReview review) =>
        (review.BlockedOrdinaryRequests > 0
            ? $"Recovery only: {review.BlockedOrdinaryRequests} unstarted part(s) are blocked because the narration source changed. This action will not complete the article MP3. "
            : string.Empty)
        + $"Voice: {review.VoiceName}. Estimated additional cost: up to {Amount(review.Currency, review.MaximumAdditionalMinorUnits, review.Scale)} (not the final Google bill). "
        + "Actual charges may differ because of taxes, credits or currency conversion. "
        + (review.BlockedOrdinaryRequests > 0
            ? "Recover authorizes only the displayed already-started work and its admitted split recovery within the existing approved spending limit. It does not authorize the blocked parts. "
            : $"Generate authorizes up to this amount for this article within the existing approved spending limit, without separate confirmations for its {review.RemainingRequests} remaining parts. ")
        + RecoveryDescription(review)
        + $"Article-generation commitments: {Amount(review.Currency, review.RetainedCommitmentsMinorUnits, review.Scale)} remain retained; this action acknowledges these existing holds. These are retained article approval baselines and reservations, not your complete Google account bill. "
        + "Accepted audio is reused. The app stops if a request has an uncertain outcome; it will not automatically pay to repeat it.";

    public static string Details(ChirpWholeArticleReview review) =>
        $"{review.AcceptedParts}/{review.TotalParts} parts already accepted. "
        + (review.CompletionBound ? "No fixed run timer: this admitted work ends on completion, its spending limit, uncertainty or your stop. "
            : $"This review is valid until {review.ConfirmByUtc:O}; existing generation authority expires {review.ExpiresAtUtc:O}. ")
        + (review.CompletionBound ? "Non-speech verification refreshes when evidence becomes stale; failure or no freshness progress stops the work. "
            : $"Up to {review.MaximumCatalogRefreshes} already-admitted non-speech verification refreshes may occur between completed requests. ")
        + "This action does not raise the configured spending ceiling or clear an unresolved charge.";

    public static string RecoveryDescription(ChirpWholeArticleReview review) => review.MaximumRecoveryRequests > 0
        ? $"This estimate includes up to {review.MaximumRecoveryRequests} additional source-preserving split requests, bounded by {Amount(review.Currency, review.MaximumRecoveryMinorUnits, review.Scale)} of the shared recovery allowance. "
            + "Only confirmed sentence-length rejections may be split at existing punctuation. Text and voice stay unchanged; uncertainty, other errors or a reached limit stop the job. Splitting does not guarantee acceptance. "
        : string.Empty;

    public static string CaptureDescription(ChirpWholeArticleReview review) =>
        (review.MaximumRecoveryRequests > 0
            ? "Private evidence required for automatic split recovery: capture every failing response for this exact article invocation, without a per-error count limit. "
            : "Optional private error evidence for this exact article: retain the first failing response. ")
        + "Each full error response is bounded to 16 KiB after redaction and encrypted for the current Windows user. All private error history is preserved for 24 hours without count-based rotation; only separate body-free success metadata retains the newest eight records. Article fragments may remain. "
        + (review.CompletionBound
            ? "Active only for this exact article invocation until completion, stop or the approved budget; no fixed active timer and no capture after restart. "
            : "Active only within this article's existing authorization, at most six hours; no capture after restart. ")
        + "Logical retention is 24 hours with deletion on next access; closed-app data and backups may remain. Excluded from ordinary logs, exports and uploads. "
        + (review.MaximumRecoveryRequests > 0 ? "Missing required evidence, disk or storage failure, uncertainty or the admitted recovery/request limit stops further paid requests. Capture itself grants no paid permission."
            : "Stops after the first failure or uncertainty; never grants paid retry permission.");

    private static string Amount(string currency, long minorUnits, int scale)
    {
        if (scale is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(scale));
        decimal divisor = 1;
        for (int index = 0; index < scale; index++) divisor *= 10;
        return currency + " " + (minorUnits / divisor).ToString("F" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
    }
}
