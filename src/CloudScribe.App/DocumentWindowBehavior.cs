using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;

namespace CloudScribe.App;

internal sealed class DocumentWindowBehavior
{
    private static readonly FilePickerFileType SupportedDocuments = new("CloudScribe documents")
    {
        Patterns = ["*.txt", "*.md", "*.markdown", "*.html", "*.htm", "*.docx"],
    };

    private readonly MainWindow _window;
    private bool _closeApproved;
    private bool _closeInProgress;

    private DocumentWindowBehavior(MainWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.KeyDown += OnKeyDown;
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;
        if (ViewModel is { } viewModel)
        {
            viewModel.ImportDocumentRequested += OnImportDocumentRequested;
        }
    }

    public static void Attach(MainWindow window) => _ = new DocumentWindowBehavior(window);

    private ShellViewModel? ViewModel => _window.DataContext as ShellViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Handled || ViewModel is not { } viewModel)
        {
            return;
        }

        if (eventArgs.KeyModifiers == KeyModifiers.Control && eventArgs.Key == Key.S)
        {
            eventArgs.Handled = Execute(viewModel.SaveDocumentCommand);
            return;
        }

        if (eventArgs.KeyModifiers == KeyModifiers.Control && eventArgs.Key == Key.N)
        {
            eventArgs.Handled = Execute(viewModel.NewDocumentCommand);
            return;
        }

        if (eventArgs.KeyModifiers == KeyModifiers.Control && eventArgs.Key == Key.O)
        {
            eventArgs.Handled = Execute(viewModel.ImportDocumentCommand);
        }
    }

    private async void OnImportDocumentRequested(object? sender, EventArgs eventArgs)
    {
        ShellViewModel? viewModel = ViewModel;
        if (viewModel is null || !_window.StorageProvider.CanOpen)
        {
            if (viewModel is not null)
            {
                viewModel.StatusMessage = "This platform cannot open the local document picker";
            }

            return;
        }

        try
        {
            IReadOnlyList<IStorageFile> files = await _window.StorageProvider
                .OpenFilePickerAsync(new()
                {
                    Title = "Import local document",
                    AllowMultiple = false,
                    FileTypeFilter = [SupportedDocuments],
                })
                .ConfigureAwait(true);
            IStorageFile? file = files.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            if (!TryResolveImportKind(file.Name, out LocalDocumentImportKind kind))
            {
                viewModel.StatusMessage = "Unsupported local document type";
                return;
            }

            using Stream content = await file.OpenReadAsync().ConfigureAwait(true);
            await viewModel.ImportDocumentAsync(kind, file.Name, content).ConfigureAwait(true);
        }
        catch (Exception)
        {
            viewModel.StatusMessage = "The local document picker failed safely · no existing document was changed";
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_closeApproved
            || _closeInProgress
            || ViewModel is not { RequiresDocumentSaveBeforeClose: true } viewModel)
        {
            return;
        }

        eventArgs.Cancel = true;
        _closeInProgress = true;
        _ = CompleteCloseAsync(viewModel);
    }

    private async Task CompleteCloseAsync(ShellViewModel viewModel)
    {
        bool canClose = await viewModel.PrepareDocumentCloseAsync().ConfigureAwait(true);
        _closeInProgress = false;
        if (!canClose)
        {
            return;
        }

        _closeApproved = true;
        _window.Close();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.ImportDocumentRequested -= OnImportDocumentRequested;
        }

        _window.KeyDown -= OnKeyDown;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
    }

    private static bool TryResolveImportKind(string fileName, out LocalDocumentImportKind kind)
    {
        string extension = Path.GetExtension(fileName);
        if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalDocumentImportKind.PlainText;
            return true;
        }

        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalDocumentImportKind.Markdown;
            return true;
        }

        if (string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalDocumentImportKind.Html;
            return true;
        }

        if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalDocumentImportKind.Docx;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool Execute(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }
}
