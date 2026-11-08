using System.Globalization;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.App.Composition;

/// <summary>Where the batch is in its journey (files → voice and output → review → start → results), what to do next,
/// and why the next button is not available yet. Pure, so every message is tested.</summary>
public static class BatchGuide
{
    public enum StepStatus { Todo, Current, Done, Blocked }

    public sealed record GuideStep(string Name, StepStatus Status);

    /// <summary>Everything the window knows that decides the next step.</summary>
    public sealed record Input(
        int Files,
        int Usable,
        bool HasVoice,
        string? VoiceProblem,
        bool VoiceMatchesBatch,
        bool HasOutputFolder,
        bool Saved,
        bool Reviewed,
        bool Approved,
        BatchAuthorityMode? Mode,
        bool ProfileInstalled,
        int Unnarratable,
        bool NeedsCaptureConsent,
        bool CaptureConsentGiven,
        bool Running,
        bool Busy,
        int Completed,
        int Failed,
        int Uncertain,
        int Remaining,
        bool Finished,
        int PendingImport = 0);

    public sealed record Result(IReadOnlyList<GuideStep> Steps, string Next, IReadOnlyList<string> SaveBlockers, IReadOnlyList<string> StartBlockers)
    {
        public string StepLine => string.Join("   ", Steps.Select((s, i) => $"{Mark(s.Status)} {i + 1} {s.Name}"));

        private static string Mark(StepStatus status) => status switch
        {
            StepStatus.Done => "✓",
            StepStatus.Current => "▶",
            StepStatus.Blocked => "✗",
            _ => "○",
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One ordered list of plain rules.")]
    public static Result For(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var save = new List<string>();
        if (input.Files == 0) save.Add("Add TXT files: drag them into the list, or choose Add TXT files… or Add a folder…");
        else if (input.Usable == 0) save.Add("None of the listed files can be used (each row says why). Add other TXT files.");
        if (!input.HasVoice) save.Add(input.VoiceProblem ?? "Choose the voice for the batch.");
        else if (input.Saved && !input.VoiceMatchesBatch) save.Add("Select this batch's own voice (Use this batch's voice).");
        if (!input.HasOutputFolder) save.Add("Choose the output folder for the MP3 files.");

        var start = new List<string>(save);
        if (!input.Saved) start.Add("Save the batch (Add to library and save batch).");
        else if (input.PendingImport > 0) start.Add($"Finish adding {input.PendingImport} file(s) to the library (reopen the batch, or remove them).");
        else if (input.Finished) start.Add("Every file is finished.");
        else if (!input.ProfileInstalled)
            start.Add("Batch spending is not set up on this computer, so no paid batch can start. You can still prepare, save and rehearse it at no cost.");
        else if (input.Mode == BatchAuthorityMode.Off) start.Add("Batch narration is turned off on this computer.");
        else if (!input.Approved)
        {
            if (input.Unnarratable > 0) start.Add($"Remove the {input.Unnarratable} file(s) no allowed engine can take.");
            start.Add(input.Mode == BatchAuthorityMode.Reviewed
                ? "Export the batch request and have it installed, then reopen the batch."
                : input.Reviewed ? "Approve the batch (the button shows its upper limit)." : "Wait for the cost review to finish.");
        }
        if (input.NeedsCaptureConsent && !input.CaptureConsentGiven) start.Add("Tick the Long Audio private-response box (required by Long Audio).");
        if (input.Running) { save.Clear(); start.Clear(); start.Add("The batch is running."); }
        if (input.Busy) start.Add("Wait for the current step to finish.");

        bool filesDone = input.Usable > 0;
        bool setupDone = filesDone && input.HasVoice && input.HasOutputFolder && (!input.Saved || input.VoiceMatchesBatch);
        bool reviewDone = input.Saved && (input.Approved || input.Finished);
        bool started = input.Completed + input.Failed + input.Uncertain > 0 || input.Running;
        var steps = new List<GuideStep>
        {
            new GuideStep("Files", filesDone ? StepStatus.Done : input.Files > 0 ? StepStatus.Blocked : StepStatus.Current),
            new GuideStep("Voice & output", setupDone ? StepStatus.Done : filesDone ? (input.HasVoice || input.VoiceProblem is null ? StepStatus.Current : StepStatus.Blocked) : StepStatus.Todo),
            new GuideStep("Review", reviewDone ? StepStatus.Done
                : !setupDone ? StepStatus.Todo
                : input.Saved && (!input.ProfileInstalled || input.Mode == BatchAuthorityMode.Off || input.Unnarratable > 0) ? StepStatus.Blocked
                : StepStatus.Current),
            new GuideStep("Start", input.Finished || started && !input.Running && input.Remaining == 0 ? StepStatus.Done
                : input.Running ? StepStatus.Current
                : reviewDone ? (start.Count == 0 ? StepStatus.Current : StepStatus.Blocked) : StepStatus.Todo),
            new GuideStep("Results", input.Finished && input.Failed + input.Uncertain == 0 ? StepStatus.Done
                : started ? (input.Failed + input.Uncertain > 0 ? StepStatus.Blocked : StepStatus.Current) : StepStatus.Todo),
        };
        return new(steps, NextFor(input, save, start), save, start);
    }

    private static string NextFor(Input input, List<string> save, List<string> start)
    {
        if (input.Running) return "Running. You can pause after the current file or stop now; finished MP3s are kept.";
        if (input.Finished)
            return input.Failed + input.Uncertain > 0
                ? $"Finished with {input.Failed} failed and {input.Uncertain} needing checking. Select a row to Retry, Check again or Skip."
                : $"All {input.Completed} MP3 file(s) are done. Open the output folder to listen.";
        if (!input.Saved) return save.Count == 0 ? "Next: choose Add to library and save batch. Nothing is sent to Google." : "Next: " + save[0];
        if (start.Count == 0) return input.Completed > 0 ? "Next: choose Resume." : "Next: choose Start. Files are narrated one at a time, in the order shown.";
        return "Next: " + start[0];
    }

    /// <summary>Tooltip text for a button that is not available, listing every reason, or the normal tip.</summary>
    public static string Explain(string normal, IReadOnlyList<string> blockers)
    {
        ArgumentNullException.ThrowIfNull(blockers);
        return blockers.Count == 0 ? normal : "Not available yet:\n• " + string.Join("\n• ", blockers);
    }
}
