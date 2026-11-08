using System.Globalization;
using CloudScribe.Application.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Plain-language Long Audio stage headings, step tracker and readable sizes.
/// Presentation only: it grants no spending, retry or completion authority.</summary>
public static class LongAudioDialogPresentation
{
    public static IReadOnlyList<string> Steps { get; } = ["Prepare", "Create speech", "Join", "Check", "Done"];

    /// <summary>Index into <see cref="Steps"/>, or -1 when the run is not progressing (stopped, failed or gap).</summary>
    public static int StepIndex(LongAudioWorkflowStage stage) => stage switch
    {
        LongAudioWorkflowStage.Reviewing or LongAudioWorkflowStage.LocalSetup => 0,
        LongAudioWorkflowStage.SubmittingOrResuming or LongAudioWorkflowStage.WaitingForGoogle
            or LongAudioWorkflowStage.CheckingGoogleAndOutput => 1,
        LongAudioWorkflowStage.JoiningArticle => 2,
        LongAudioWorkflowStage.VerifyingArticle => 3,
        LongAudioWorkflowStage.Completed => 4,
        _ => -1,
    };

    public static string Headline(LongAudioWorkflowStage stage) => stage switch
    {
        LongAudioWorkflowStage.Reviewing => "Ready to review",
        LongAudioWorkflowStage.LocalSetup => "Getting ready",
        LongAudioWorkflowStage.SubmittingOrResuming => "Sending to Google",
        LongAudioWorkflowStage.WaitingForGoogle => "Google is creating the speech",
        LongAudioWorkflowStage.CheckingGoogleAndOutput => "Downloading and checking audio",
        LongAudioWorkflowStage.JoiningArticle => "Joining the article into one MP3",
        LongAudioWorkflowStage.VerifyingArticle => "Final check of the finished MP3",
        LongAudioWorkflowStage.Completed => "Your MP3 is ready",
        LongAudioWorkflowStage.Stopped => "Paused: you can resume later",
        LongAudioWorkflowStage.DeferredGap => "Almost done: one section needs attention",
        _ => "Needs attention",
    };

    public static string StepState(int step, int current) =>
        current < 0 ? "not active" : step < current ? "done" : step == current ? "current" : "not started";

    public static string ReadableSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        const double Kib = 1024d, Mib = Kib * 1024, Gib = Mib * 1024;
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        string unit = bytes >= Gib ? (bytes / Gib).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : bytes >= Mib ? (bytes / Mib).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : (bytes / Kib).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        return unit + " (" + bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes)";
    }
}
