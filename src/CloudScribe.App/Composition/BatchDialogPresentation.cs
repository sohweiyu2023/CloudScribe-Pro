using System.Globalization;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.App.Composition;

/// <summary>Plain-language text for the batch window. Amounts are estimates and holds, never a bill.</summary>
public static class BatchDialogPresentation
{
    public static string Row(BatchItem item, BatchAuthorityProfile? profile) => Title(item) + "\n      " + Detail(item, profile);

    public static string Title(BatchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string order = item.Order.ToString("00", CultureInfo.InvariantCulture);
        return item.IsIncluded ? $"{order}  {item.SourceFileName}  →  {item.OutputFileName}" : $"{order}  {item.SourceFileName}";
    }

    public static string Detail(BatchItem item, BatchAuthorityProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsIncluded) return "skipped: " + Exclusion(item.Exclusion);
        string engine = item.Engine is null ? string.Empty : " · " + BatchEngines.Describe(item.Engine);
        string estimate = profile is null ? string.Empty
            : " · up to " + BatchMoney.Format(item.EstimatedMaximumMinorUnits > 0 ? item.EstimatedMaximumMinorUnits : profile.EstimateMinorUnits(item.Characters), profile);
        string same = item.SameTextAs is { } earlier ? $" · same text as file {earlier.ToString("00", CultureInfo.InvariantCulture)} (kept)" : string.Empty;
        return $"{item.Characters.ToString("N0", CultureInfo.InvariantCulture)} characters{engine}{estimate}{same} · {State(item)}";
    }

    /// <summary>Colour role for a row: done, attention (needs checking or running), failed, waiting or muted.</summary>
    public static string Tone(BatchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.State switch
        {
            BatchItemState.Completed => "done",
            BatchItemState.Uncertain or BatchItemState.Running => "attention",
            BatchItemState.Failed => "failed",
            BatchItemState.Ready => "waiting",
            _ => "muted",
        };
    }

    public static string QueueState(BatchQueueState state) => state switch
    {
        BatchQueueState.Ready => "not started",
        BatchQueueState.Running => "running",
        BatchQueueState.Paused => "paused",
        _ => "finished",
    };

    public static string State(BatchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string reason = item.ReasonCode is { } code ? " — " + Reason(code) : string.Empty;
        return item.State switch
        {
            BatchItemState.Ready when item.JobId is not null => "waiting (will continue its saved job)" + reason,
            BatchItemState.Ready => "waiting" + reason,
            BatchItemState.Running => "in progress",
            BatchItemState.Completed => "done" + (item.ActiveMilliseconds > 0 ? $" in {Duration(TimeSpan.FromMilliseconds(item.ActiveMilliseconds))}" : string.Empty),
            BatchItemState.Failed => "failed" + reason,
            BatchItemState.Uncertain => "needs checking" + reason,
            BatchItemState.Skipped => "skipped by you",
            _ => "not included",
        };
    }

    public static string Exclusion(BatchExclusion exclusion) => exclusion switch
    {
        BatchExclusion.Empty => "the file has no text",
        BatchExclusion.NotUnicodeText => "not UTF-8 or UTF-16 text; save it as UTF-8 and add it again",
        BatchExclusion.TooLarge => "larger than 32 MB",
        BatchExclusion.Unreadable => "could not be read",
        BatchExclusion.Duplicate => "same content as an earlier file",
        BatchExclusion.Unsupported => "only .txt files can be batched",
        BatchExclusion.RemovedByUser => "removed by you",
        _ => "not included",
    };

    public static string Reason(string code) => code switch
    {
        "batch.article-review-refused-nothing-sent" => "this file is not admitted for narration yet; nothing was sent",
        "batch.long-audio-not-admitted-nothing-sent" => "Long Audio has not admitted this file (approval missing, expired or over the cap); nothing was sent",
        "batch.long-audio-needs-reconciliation" => "its Long Audio request must be checked first; nothing new was sent",
        "batch.long-audio-stopped-check-again" => "stopped while waiting; Check again continues the same request",
        "batch.long-audio-failed-check-again" => "Long Audio reported a problem; Check again looks at the same request, it is never resent",
        "batch.long-audio-output-not-verified" => "the audio is not verified yet; Check again downloads and checks it without new speech",
        "batch.long-audio-output-not-mp3-speech-kept" => "the saved audio is not an MP3; speech is kept, nothing was resent",
        "batch.local-copy-failed-speech-kept" => "the MP3 could not be copied to the output folder; speech is saved, Retry copies it again with no new request",
        "batch.output-not-verified" => "the MP3 in the output folder did not match; Retry copies the saved audio again",
        "batch.engine-changed-after-start" => "its engine differs from the one it started with; it is never switched",
        "batch.article-needs-reconciliation" => "an earlier request must be reconciled first; nothing new was sent",
        "batch.plan-changed-nothing-sent" => "its narration plan differs from the approved one; nothing was sent",
        "batch.file-over-approved-maximum-nothing-sent" => "its exact maximum is above the approved amount; nothing was sent",
        "batch.currency-mismatch-nothing-sent" => "currency differs from the approval; nothing was sent",
        "batch.file-not-approved" => "not covered by the batch approval",
        "batch.output-missing-or-changed" => "the finished MP3 is missing or changed; it was not regenerated",
        "batch.import-interrupted-add-again" => "the app closed while adding it; add the file again",
        "batch.narration-failed-before-start" => "stopped before anything was sent",
        "batch.narration-failed-after-start-reconcile" => "stopped after it started; check it before retrying",
        "batch.paused" => "paused",
        "batch.not-authorized" => "not approved",
        "article.provider-rejected-no-retry" => "Google rejected a request",
        "article.provider-outcome-uncertain-no-retry" => "Google's answer was not received; it will not be resent automatically",
        _ when code.EndsWith("-reconcile", StringComparison.Ordinal) => "stopped; check it before retrying",
        _ => code,
    };

    public static string Summary(NarrationBatch batch, BatchAuthorityProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(batch);
        int failed = batch.Items.Count(x => x.State == BatchItemState.Failed);
        int uncertain = batch.Items.Count(x => x.State == BatchItemState.Uncertain);
        int excluded = batch.Items.Count(x => !x.IsIncluded);
        string text = $"{batch.CompletedCount} of {batch.IncludedCount} done";
        if (failed > 0) text += $" · {failed} failed";
        if (uncertain > 0) text += $" · {uncertain} need checking";
        if (excluded > 0) text += $" · {excluded} not included";
        if (profile is null) return text + ".";
        long estimate = batch.Included.Sum(x => x.EstimatedMaximumMinorUnits > 0 ? x.EstimatedMaximumMinorUnits : profile.EstimateMinorUnits(x.Characters));
        return text + $".\nEstimated maximum for the batch: {BatchMoney.Format(estimate, profile)}. Held so far: {BatchMoney.Format(batch.ReservedMinorUnits, profile)}."
            + "\nThese are estimates and holds, not your Google bill.";
    }

    public static string Timing(NarrationBatch batch, TimeSpan currentRun)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var active = TimeSpan.FromMilliseconds(batch.ActiveMilliseconds) + currentRun;
        if (active <= TimeSpan.Zero) return "Not started yet.";
        string text = $"Active time {Duration(active)}";
        var done = batch.Included.Where(x => x.State == BatchItemState.Completed && x.ActiveMilliseconds > 0).ToArray();
        int remaining = batch.Included.Count(x => x.State is BatchItemState.Ready or BatchItemState.Running);
        if (done.Length >= 2 && remaining > 0)
        {
            double perCharacter = done.Sum(x => (double)x.ActiveMilliseconds) / Math.Max(1, done.Sum(x => x.Characters));
            double left = batch.Included.Where(x => x.State is BatchItemState.Ready or BatchItemState.Running).Sum(x => x.Characters) * perCharacter;
            text += $" · roughly {Duration(TimeSpan.FromMilliseconds(left))} left (based on finished files; provider waits vary)";
        }
        return text + ".";
    }

    public static double OverallPercentage(NarrationBatch batch, BatchItemProgress? current)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.IncludedCount == 0) return 0;
        double partial = current is { TotalParts: > 0 } p ? Math.Clamp((double)p.SavedParts / p.TotalParts, 0, 1) : 0;
        return Math.Min(100, 100 * (batch.CompletedCount + partial) / batch.IncludedCount);
    }

    public static string Authority(BatchAuthorityProfile? profile)
    {
        if (profile is null) return "Batch narration is not set up on this computer. You can still prepare, rehearse and save batches.";
        string limits = $"limit {BatchMoney.Format(profile.PerBatchCeilingMinorUnits, profile)} per batch and {BatchMoney.Format(profile.RollingCeilingMinorUnits, profile)} per {profile.RollingWindowDays} days";
        return profile.Mode switch
        {
            BatchAuthorityMode.InAppWithinLimits => $"You can approve batches here, within the {limits}.",
            BatchAuthorityMode.Reviewed => $"Batches are approved by review: export the batch request and have it installed ({limits}).",
            _ => "Batch narration is turned off on this computer. You can still prepare and rehearse batches.",
        };
    }

    /// <summary>One paragraph summarising a prepared review: engines, Long Audio cap room, and anything that will not run.</summary>
    public static string ReviewSummary(int longAudio, int ordinary, int needingAdmission, int unnarratable, long totalMinorUnits,
        long longAudioUsdCents, long? headroomUsdCents, BatchAuthorityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var lines = new List<string>
        {
            $"Review: {longAudio} file(s) by Long Audio, {ordinary} by the ordinary engine. Approval covers up to {BatchMoney.Format(totalMinorUnits, profile)} in total (an upper limit, not a bill).",
        };
        if (longAudio > 0 && headroomUsdCents is { } room)
            lines.Add(longAudioUsdCents <= room
                ? $"Long Audio needs up to {BatchMoney.Format(longAudioUsdCents, "USD", 2, profile.DisplayCurrency, profile.DisplayRate)}; room left in the installed Long Audio cap: {BatchMoney.Format(room, "USD", 2, profile.DisplayCurrency, profile.DisplayRate)}."
                : $"Long Audio needs up to {BatchMoney.Format(longAudioUsdCents, "USD", 2, profile.DisplayCurrency, profile.DisplayRate)}, more than the {BatchMoney.Format(room, "USD", 2, profile.DisplayCurrency, profile.DisplayRate)} left in the installed Long Audio cap. Raise the cap with the reviewed installer, or remove files.");
        if (needingAdmission > 0) lines.Add($"{needingAdmission} ordinary-engine file(s) still need an installed article admission; until then they stop with nothing sent.");
        if (unnarratable > 0) lines.Add($"{unnarratable} file(s) cannot be narrated by any allowed engine; remove them before approving (each row says why).");
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<string> EngineChoices { get; } =
        ["Long Audio first, ordinary engine where needed (recommended)", "Ordinary engine only", "Long Audio only"];

    public static string EngineForChoice(int index) => index switch
    {
        1 => BatchEngines.CompleteArticle,
        2 => BatchEngines.LongAudio,
        _ => BatchEngines.LongAudioFirst,
    };

    public static string Duration(TimeSpan value) => value.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours} h {value.Minutes} min")
        : value.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{value.Minutes} min {value.Seconds} s")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, value.Seconds)} s");
}
