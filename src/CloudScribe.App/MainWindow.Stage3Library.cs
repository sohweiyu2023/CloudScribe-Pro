using Avalonia;
using CloudScribe.App.ViewModels;
using CloudScribe.App.Views;

namespace CloudScribe.App;

public sealed partial class MainWindow
{
    static MainWindow()
    {
        DataContextProperty.Changed.AddClassHandler<MainWindow>(HandleStage3ShellContext);
    }

    private static void HandleStage3ShellContext(MainWindow window, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is ShellViewModel)
        {
            DocumentLibraryPanelMount.Attach(window);
        }
    }
}
