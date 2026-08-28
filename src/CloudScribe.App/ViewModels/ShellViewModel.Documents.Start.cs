using Avalonia.Threading;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    public void ScheduleDocumentWorkspaceStart()
    {
        if (IsStage2VisualCaptureRequested())
        {
            return;
        }

        Dispatcher.UIThread.Post(
            StartDocumentWorkspace,
            DispatcherPriority.Loaded);
    }

    private static bool IsStage2VisualCaptureRequested()
    {
        string? mode = Environment.GetEnvironmentVariable("CLOUDSCRIBE_STAGE2_CAPTURE_MODE");
        string? outputDirectory = Environment.GetEnvironmentVariable("CLOUDSCRIBE_STAGE2_CAPTURE_DIR");
        string? sourceManifestSha256 = Environment.GetEnvironmentVariable("CLOUDSCRIBE_SOURCE_MANIFEST_SHA256");
        return string.Equals(mode, "1", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(outputDirectory)
            && sourceManifestSha256 is { Length: 64 }
            && sourceManifestSha256.All(static character => Uri.IsHexDigit(character));
    }
}
