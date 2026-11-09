using System.Globalization;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.App.Composition;

/// <summary>Plain-language text for the batch window. Amounts are estimates and holds, never a bill.</summary>
public static class BatchDialogPresentation
{
    public const string CheckAgainGuidance = "Marks this saved job for rechecking when you resume the batch. It never repeats an uncertain request. Without a saved Google operation ID, CloudScribe cannot check that request with Google; review may still be needed.";

    public static string Row(BatchItem item, BatchAuthorityProfile? profile, string? earlierName = null) => Title(item) + "\n      " + Detail(item, profile, earlierName);

    public static string Title(BatchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string order = item.Order.ToString("00", CultureInfo.InvariantCulture);
        return item.IsIncluded ? $"{order}  {item.SourceFileName}  →  {item.OutputFileName}" : $"{order}  {item.SourceFileName}";
    }

    /// <summary>One row's detail line; <paramref name="earlierName"/> names the earlier file with identical text, when known.</summary>
    public static string Detail(BatchItem item, BatchAuthorityProfile? profile, string? earlierName = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsIncluded) return "skipped: " + Exclusion(item.Exclusion);
        string engine = item.Engine is null ? string.Empty : " · " + BatchEngines.Describe(item.Engine);
        string estimate = profile is null ? string.Empty
            : " · up to " + BatchMoney.Format(item.EstimatedMaximumMinorUnits > 0 ? item.EstimatedMaximumMinorUnits : profile.EstimateMinorUnits(item.Characters), profile);
        string same = item.SameTextAs is { } earlier ? $" · same text as file {earlier.ToString("00", CultureInfo.InvariantCulture)}{(earlierName is null ? string.Empty : $" ({earlierName})")}, kept as its own track" : string.Empty;
        string earlierCharge = item.UncertainOutcomes > 0
            ? " · an earlier request for this file may have reached Google and may be charged; check your Google bill" : string.Empty;
        return $"{item.Characters.ToString("N0", CultureInfo.InvariantCulture)} characters{engine}{estimate}{same} · {State(item)}{earlierCharge}";
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
        BatchExclusion.NarrationAnalysisLimit => "URL analysis reached its safety limit; simplify malformed links or prepare a new batch with the option off",
        BatchExclusion.NarrationTooLarge => "URL skipping supports up to 16 million text units; split this file or prepare a new batch with the option off",
        BatchExclusion.NoSpeakableText => "no spoken words remain after skipping web addresses; original kept, no request sent",
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
        "batch.article-output-preflight-nothing-sent" => "output folder, filename or approved audio tools are not ready; choose a new MP3 name in a physical folder and check audio setup, then Retry. No article job or speech request started",
        "batch.article-output-preflight-reconcile" => "local output or audio-tool checks stopped the next speech request. Earlier audio, jobs and holds are kept; fix output/audio setup, then Check again to reconcile before continuing",
        "batch.article-review-refused-nothing-sent" => "this file is not admitted for narration yet; nothing was sent",
        "batch.long-audio-not-admitted-nothing-sent" => "Long Audio has not admitted this file (approval missing, expired or over the cap); nothing was sent",
        "batch.long-audio-needs-reconciliation" => "its Long Audio request needs review; nothing new was sent. Without a saved Google operation ID, its status cannot be checked with Google",
        "batch.long-audio-stopped-check-again" => "stopped; saved job preserved. Check again marks it for rechecking when you resume",
        "batch.long-audio-failed-check-again" => "Long Audio needs attention; Check again marks the saved job for rechecking when you resume. An uncertain request is not resent",
        "batch.long-audio-output-not-verified" => "the audio is not verified yet; Check again marks the saved job for rechecking when you resume",
        "batch.long-audio-output-not-mp3-speech-kept" => "the saved audio is not an MP3; speech is kept, nothing was resent",
        "batch.local-copy-failed-speech-kept" => "the MP3 could not be copied to the output folder; speech is saved, Retry copies it again with no new request",
        "batch.output-not-verified" => "the MP3 in the output folder did not match; Retry copies the saved audio again",
        "batch.engine-changed-after-start" => "its engine differs from the one it started with; it is never switched",
        "batch.article-needs-reconciliation" => "an earlier request must be reconciled first; nothing new was sent",
        "batch.plan-changed-nothing-sent" => "its narration plan differs from the approved one; nothing was sent",
        "batch.file-over-approved-maximum-nothing-sent" => "its exact maximum is above the approved amount; nothing was sent",
        "batch.currency-mismatch-nothing-sent" => "currency differs from the approval; nothing was sent",
        "batch.narration-changed" => "the original or narration copy changed; prepare and review a new batch, nothing sent",
        "batch.narration-validation-unavailable" => "the saved narration copy cannot be verified in this build",
        "batch.narration-version" => "the URL skipping version is unsupported; prepare a new batch",
        "batch.file-not-approved" => "not covered by the batch approval",
        "batch.output-missing-or-changed" => "the finished MP3 is missing or changed; it was not regenerated",
        "batch.import-interrupted-add-again" => "the app closed while adding it; add the file again",
        "batch.source-changed-add-again" => "the TXT file changed after it was listed; remove it and add it again so the new text is reviewed",
        "batch.source-missing-add-again" => "the TXT file could not be read again (moved, renamed or deleted); add it again",
        "batch.source-import-refused" => "the library could not accept this file; remove it, or rename the TXT and add it again (other files were added normally)",
        "batch.narration-failed-before-start" => "stopped before anything was sent",
        "batch.narration-failed-after-start-reconcile" => "stopped after it started; check it before retrying",
        "batch.paused" => "paused",
        "batch.not-authorized" => "not approved",
        "article.narration-copy-recovery-only" => "recovered audio is retained; remaining fresh parts are blocked because the original changed. Prepare a new batch from the original TXT or save its text as a new document for a new narration copy",
        "article.provider-rejected-no-retry" => "Google rejected a request",
        "article.provider-outcome-uncertain-no-retry" => "Google's answer was not received; it will not be resent automatically",
        _ when code.EndsWith("-reconcile", StringComparison.Ordinal) => "stopped; check it before retrying",
        _ => code,
    };

    public static string Summary(NarrationBatch batch, BatchAuthorityProfile? profile, long? approvedMinorUnits = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var counts = Counts(batch);
        string text = $"{counts.Completed} of {batch.IncludedCount} done";
        if (counts.Failed > 0) text += $" · {counts.Failed} failed";
        if (counts.Uncertain > 0) text += $" · {counts.Uncertain} need checking";
        if (counts.Skipped > 0) text += $" · {counts.Skipped} skipped";
        if (counts.NotIncluded > 0) text += $" · {counts.NotIncluded} not included";
        if (profile is null) return text + ".";
        return text + ".\n" + Money(batch, profile, approvedMinorUnits);
    }

    /// <summary>The three amounts the window shows, each labelled for what it is, none of them a bill: the estimated
    /// maximum, the approved allowance, and holds and possible charges from requests that may have reached Google.</summary>
    public static string Money(NarrationBatch batch, BatchAuthorityProfile profile, long? approvedMinorUnits)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(profile);
        long estimate = batch.Included.Sum(x => x.EstimatedMaximumMinorUnits > 0 ? x.EstimatedMaximumMinorUnits : profile.EstimateMinorUnits(x.Characters));
        var lines = new List<string>
        {
            $"Estimate (upper limit, not a bill): up to {BatchMoney.Format(estimate, profile)} for the batch.",
            approvedMinorUnits is { } approved ? $"Approved allowance: up to {BatchMoney.Format(approved, profile)}." : "Approved allowance: none yet.",
        };
        int possible = batch.Items.Count(x => x.UncertainOutcomes > 0 || x.State == BatchItemState.Uncertain);
        lines.Add(batch.ReservedMinorUnits > 0 || possible > 0
            ? $"Held for files that started: {BatchMoney.Format(batch.ReservedMinorUnits, profile)}" + (possible > 0 ? $"; {possible} file(s) had a request that may have reached Google, so a charge is possible even where new audio exists." : ".")
            : "Held for files that started: nothing yet.");
        lines.Add("Your Google Cloud bill is the only record of actual charges.");
        return string.Join('\n', lines);
    }

    public sealed record ResultCounts(int Completed, int Failed, int Uncertain, int Skipped, int NotIncluded, int Waiting);

    public static ResultCounts Counts(NarrationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new(batch.Items.Count(x => x.State == BatchItemState.Completed), batch.Items.Count(x => x.State == BatchItemState.Failed),
            batch.Items.Count(x => x.State == BatchItemState.Uncertain), batch.Items.Count(x => x.State == BatchItemState.Skipped),
            batch.Items.Count(x => x.State == BatchItemState.Excluded), batch.Items.Count(x => x.State is BatchItemState.Ready or BatchItemState.Running));
    }

    /// <summary>One line for the results area: completed, failed, skipped and needing checking, kept apart.</summary>
    public static string Results(NarrationBatch batch)
    {
        var c = Counts(batch);
        if (c.Completed + c.Failed + c.Uncertain + c.Skipped == 0) return string.Empty;
        return $"Results: {c.Completed} MP3 done · {c.Failed} failed · {c.Uncertain} need checking · {c.Skipped} skipped by you · {c.Waiting} waiting."
            + (c.Failed > 0 ? " A failed local copy can be retried without new speech." : string.Empty)
            + (c.Uncertain > 0 ? " Needs checking is never resent automatically." : string.Empty);
    }

    /// <summary>The draft's summary line before it is saved.</summary>
    public static string DraftSummary(IReadOnlyList<BatchItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return string.Empty;
        int included = items.Count(x => x.IsIncluded);
        int same = items.Count(x => x.IsIncluded && x.SameTextAs is not null);
        return $"{included} of {items.Count} files will be narrated, one MP3 each, in this order. Drag rows (or use Alt+Up / Alt+Down) to change it."
            + (same > 0 ? $" {same} file(s) have the same text as an earlier file; each is still its own track." : string.Empty);
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
        if (profile is null) return "Batch spending is not set up on this computer, so a paid batch cannot start yet. You can prepare files now; saving or rehearsing also requires a verified voice. Preparing, saving and rehearsing cost nothing. The batch guide explains how spending limits are set up.";
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
