using Avalonia.Threading;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    public void ScheduleDocumentWorkspaceStart()
    {
        Dispatcher.UIThread.Post(
            StartDocumentWorkspace,
            DispatcherPriority.Loaded);
    }
}
