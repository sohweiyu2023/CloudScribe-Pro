using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CloudScribe.App.ViewModels;
using CloudScribe.App.Views;
using CloudScribe.Application.Documents;

namespace CloudScribe.App;

public sealed partial class MainWindow
{
    private const string FinalEmptyLifecycleDescription =
        "No document is selected. Create a local document or import supported text formats to begin.";
    private const string FinalErrorLifecycleDescription =
        "Existing local documents were left unchanged. Retry the local operation or use the available recovery actions.";

    static MainWindow()
    {
        DataContextProperty.Changed.AddClassHandler<MainWindow>(HandleStage3ShellContext);
        KeyDownEvent.AddClassHandler<MainWindow>(HandleStage3ClipboardShortcut);
    }

    private static void HandleStage3ShellContext(MainWindow window, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is ShellViewModel viewModel)
        {
            DocumentLibraryPanelMount.Attach(window);
            ReplaceLegacyStage2WorkspaceCopy(window, viewModel);

            // The initial DataContext is assigned before Avalonia has necessarily built the
            // complete visual tree. Re-run the production copy replacement after Opened so
            // first-launch users never see the historical staged-shell text.
            window.Opened -= HandleStage3WindowOpened;
            window.Opened += HandleStage3WindowOpened;

            // Lifecycle state can change after Opened while the durable local library is
            // initialized. Observe those changes so an Empty/Error transition cannot restore
            // historical Stage 3 placeholder copy into a Final build.
            viewModel.PropertyChanged += (_, args) =>
            {
                if (string.Equals(
                    args.PropertyName,
                    nameof(ShellViewModel.LifecycleDescription),
                    StringComparison.Ordinal))
                {
                    Dispatcher.UIThread.Post(
                        () => ReplaceLegacyStage2WorkspaceCopy(window, viewModel),
                        DispatcherPriority.Background);
                }
            };
        }
    }

    private static void HandleStage3WindowOpened(object? sender, EventArgs eventArgs)
    {
        if (sender is not MainWindow window || window.DataContext is not ShellViewModel viewModel)
        {
            return;
        }

        DocumentLibraryPanelMount.Attach(window);
        ReplaceLegacyStage2WorkspaceCopy(window, viewModel);
    }

    private static void ReplaceLegacyStage2WorkspaceCopy(MainWindow window, ShellViewModel viewModel)
    {
        string lifecycleDescription = viewModel.LifecycleDescription switch
        {
            "No document is selected. Durable document creation arrives in Stage 3." => FinalEmptyLifecycleDescription,
            "Your temporary text remains in the editor. Retry and recovery actions become durable in Stage 3." => FinalErrorLifecycleDescription,
            string current => current,
        };

        foreach (TextBlock textBlock in window.GetVisualDescendants().OfType<TextBlock>())
        {
            switch (textBlock.Text)
            {
                case "STAGE 2 PREVIEW":
                    textBlock.Text = "LOCAL AUTOSAVE";
                    break;
                case "Edits are temporary until Stage 3 durable documents and autosave are implemented.":
                    textBlock.Text = "Edits are saved locally with debounced autosave; Ctrl+S creates an explicit checkpoint.";
                    break;
                case "UNSAVED PREVIEW":
                    textBlock.Bind(TextBlock.TextProperty, new Binding(nameof(ShellViewModel.DocumentSaveState))
                    {
                        Source = viewModel,
                    });
                    break;
                case "No document is selected. Durable creation and import arrive in Stage 3; this Stage 2 state intentionally contains no editable sample text or inactive creation control.":
                    textBlock.Text = "No document is selected. Create a local document or import TXT, Markdown, HTML, DOCX, or clipboard text to begin.";
                    break;
                case "No document is selected. Durable document creation arrives in Stage 3.":
                case "Your temporary text remains in the editor. Retry and recovery actions become durable in Stage 3.":
                case FinalEmptyLifecycleDescription:
                case FinalErrorLifecycleDescription:
                    textBlock.Text = lifecycleDescription;
                    break;
            }
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
