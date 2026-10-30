using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private const int LibraryPageSize = 200;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousLibraryPageCommand), nameof(NextLibraryPageCommand))]
    private int _libraryPageOffset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousLibraryPageCommand), nameof(NextLibraryPageCommand))]
    private int _libraryTotalArticles;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousLibraryPageCommand), nameof(NextLibraryPageCommand))]
    private bool _libraryPagingAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousLibraryPageCommand), nameof(NextLibraryPageCommand))]
    private bool _isLibraryPageLoading;

    [ObservableProperty]
    private string _libraryPageStatus = "Loading local articles.";

    private bool CanPreviousLibraryPage() => LibraryPagingAvailable && !IsLibraryPageLoading && LibraryPageOffset > 0;
    private bool CanNextLibraryPage() => LibraryPagingAvailable && !IsLibraryPageLoading
        && (long)LibraryPageOffset + LibraryPageSize < LibraryTotalArticles;

    [RelayCommand(CanExecute = nameof(CanPreviousLibraryPage))]
    private async Task PreviousLibraryPageAsync()
    {
        if (!CanPreviousLibraryPage()) return;
        LibraryPageOffset = Math.Max(0, LibraryPageOffset - LibraryPageSize);
        await RefreshDocumentLibraryObservedAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanNextLibraryPage))]
    private async Task NextLibraryPageAsync()
    {
        if (!CanNextLibraryPage()) return;
        LibraryPageOffset += LibraryPageSize;
        await RefreshDocumentLibraryObservedAsync().ConfigureAwait(true);
    }
}
