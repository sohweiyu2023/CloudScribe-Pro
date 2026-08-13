using Avalonia.Controls;
using Avalonia.Input;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App;

internal sealed class DocumentWindowBehavior
{
    private readonly MainWindow _window;
    private bool _closeApproved;
    private bool _closeInProgress;

    private DocumentWindowBehavior(MainWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.KeyDown += OnKeyDown;
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;
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
        _window.KeyDown -= OnKeyDown;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
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
