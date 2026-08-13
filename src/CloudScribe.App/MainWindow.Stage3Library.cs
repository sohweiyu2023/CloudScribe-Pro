using System.Text;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CloudScribe.App.ViewModels;
using CloudScribe.App.Views;
using CloudScribe.Application.Documents;

namespace CloudScribe.App;

public sealed partial class MainWindow
{
    static MainWindow()
    {
        DataContextProperty.Changed.AddClassHandler<MainWindow>(HandleStage3ShellContext);
        KeyDownEvent.AddClassHandler<MainWindow>(HandleStage3ClipboardShortcut);
    }

    private static void HandleStage3ShellContext(MainWindow window, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is ShellViewModel)
        {
            DocumentLibraryPanelMount.Attach(window);
        }
    }

    private static void HandleStage3ClipboardShortcut(MainWindow window, KeyEventArgs eventArgs)
    {
        KeyModifiers expected = KeyModifiers.Control | KeyModifiers.Shift;
        if (eventArgs.Handled
            || eventArgs.Key != Key.V
            || eventArgs.KeyModifiers != expected
            || window.DataContext is not ShellViewModel viewModel)
        {
            return;
        }

        eventArgs.Handled = true;
        _ = ImportClipboardObservedAsync(window, viewModel);
    }

    private static async Task ImportClipboardObservedAsync(MainWindow window, ShellViewModel viewModel)
    {
        try
        {
            IClipboard? clipboard = window.Clipboard;
            if (clipboard is null)
            {
                viewModel.StatusMessage = "Clipboard service is unavailable on this platform";
                return;
            }

            string? text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(text))
            {
                viewModel.StatusMessage = "Clipboard does not contain text";
                return;
            }

            using MemoryStream content = new(Encoding.UTF8.GetBytes(text), writable: false);
            await viewModel
                .ImportDocumentAsync(LocalDocumentImportKind.Clipboard, "Clipboard.txt", content)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            viewModel.StatusMessage = "Clipboard import failed safely · existing documents were unchanged";
        }
    }
}
