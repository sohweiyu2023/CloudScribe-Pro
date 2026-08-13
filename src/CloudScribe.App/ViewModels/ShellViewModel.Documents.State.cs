using System.Collections.ObjectModel;
using System.ComponentModel;
using CloudScribe.App.Design;
using CloudScribe.App.Navigation;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private IDocumentLibrary? _documentLibrary;
    private DocumentAutosaveCoordinator? _documentAutosave;
    private Guid? _currentDocumentId;
    private long _currentDocumentConcurrencyVersion;
    private Guid? _currentRevisionId;
    private long _autosaveSequence;
    private int _documentWorkspaceStarted;
    private bool _suppressDocumentChangeTracking;

    public ObservableCollection<DocumentSummary> LocalDocuments { get; } = [];

    [ObservableProperty]
    private bool _isDocumentDirty;

    [ObservableProperty]
    private bool _isDocumentSaving;

    [ObservableProperty]
    private bool _hasDocumentConflict;

    [ObservableProperty]
    private string _documentSaveState = "Local document";

    [ObservableProperty]
    private string _librarySearchQuery = string.Empty;

    public bool HasOpenDocument => _currentDocumentId.HasValue;

    public bool RequiresDocumentSaveBeforeClose => HasOpenDocument && IsDocumentDirty;

    public Guid? CurrentDocumentId => _currentDocumentId;

    public Guid? CurrentRevisionId => _currentRevisionId;

    public void ConfigureStage3DocumentWorkflow(
        IDocumentLibrary documentLibrary,
        DocumentAutosaveCoordinator documentAutosave)
    {
        ArgumentNullException.ThrowIfNull(documentLibrary);
        ArgumentNullException.ThrowIfNull(documentAutosave);
        if (_documentLibrary is not null || _documentAutosave is not null)
        {
            throw new InvalidOperationException("The Stage 3 document workflow is already configured.");
        }

        _documentLibrary = documentLibrary;
        _documentAutosave = documentAutosave;
        PropertyChanged += OnDocumentWorkspacePropertyChanged;

        RoutePageViewModel libraryPage = _pages[AppRoute.Library];
        libraryPage.StateKind = "LOCAL";
        libraryPage.StateTitle = "Durable local document library";
        libraryPage.StateDescription = "Documents, autosaves and explicit checkpoints stay local and work without provider credentials.";
        libraryPage.Detail = "Loading local documents…";
        libraryPage.HasPrimaryAction = true;
        libraryPage.PrimaryActionLabel = "New document";
        libraryPage.PrimaryActionCommand = NewDocumentCommand;
    }

    public void StartDocumentWorkspace()
    {
        EnsureDocumentWorkflowConfigured();
        if (Interlocked.Exchange(ref _documentWorkspaceStarted, 1) != 0)
        {
            return;
        }

        _ = InitializeDocumentWorkspaceObservedAsync();
    }

    [RelayCommand]
    private async Task NewDocumentAsync()
    {
        EnsureDocumentWorkflowConfigured();
        if (RequiresDocumentSaveBeforeClose
            && !await SaveCurrentDocumentCoreAsync("Pre-new-document checkpoint").ConfigureAwait(true))
        {
            return;
        }

        IsDocumentSaving = true;
        DocumentSaveState = "Creating…";
        try
        {
            DocumentSnapshot snapshot = await _documentLibrary!
                .CreateAsync("Untitled document", string.Empty)
                .ConfigureAwait(true);
            ApplyDocumentSnapshot(snapshot, "New local document created");
            await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
            SelectedNavigationItem = NavigationItems.First(item => item.Route == AppRoute.Studio);
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            IsDocumentSaving = false;
            IsDocumentDirty = false;
            DocumentSaveState = "Create failed";
            StatusMessage = "Local document could not be created";
        }
    }

    [RelayCommand]
    private async Task OpenDocumentAsync(Guid documentId)
    {
        EnsureDocumentWorkflowConfigured();
        if (documentId == Guid.Empty)
        {
            return;
        }

        if (RequiresDocumentSaveBeforeClose
            && !await SaveCurrentDocumentCoreAsync("Pre-open checkpoint").ConfigureAwait(true))
        {
            return;
        }

        await OpenDocumentCoreAsync(documentId).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshDocumentLibraryAsync() =>
        await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);

    partial void OnLibrarySearchQueryChanged(string value)
    {
        if (Volatile.Read(ref _documentWorkspaceStarted) != 0)
        {
            _ = RefreshDocumentLibraryObservedAsync();
        }
    }

    private async Task InitializeDocumentWorkspaceObservedAsync()
    {
        LifecycleState = WorkspaceLifecycleState.Loading;
        DocumentSaveState = "Loading local documents…";
        try
        {
            await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
            DocumentSummary? first = LocalDocuments.FirstOrDefault();
            if (first is null)
            {
                ClearDocumentWorkspace();
                return;
            }

            await OpenDocumentCoreAsync(first.Id).ConfigureAwait(true);
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            IsDocumentSaving = false;
            IsDocumentDirty = false;
            HasDocumentConflict = false;
            DocumentSaveState = "Recovery required";
            LifecycleState = WorkspaceLifecycleState.Error;
            StatusMessage = "Local document library could not be opened · existing files were left unchanged";
        }
    }

    private async Task RefreshDocumentLibraryObservedAsync()
    {
        try
        {
            await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            StatusMessage = "Local document search could not be refreshed";
        }
    }

    private async Task RefreshDocumentLibraryCoreAsync()
    {
        EnsureDocumentWorkflowConfigured();
        IReadOnlyList<DocumentSummary> documents = string.IsNullOrWhiteSpace(LibrarySearchQuery)
            ? await _documentLibrary!.ListAsync(limit: 200).ConfigureAwait(true)
            : await _documentLibrary!.SearchAsync(LibrarySearchQuery.Trim(), limit: 200).ConfigureAwait(true);

        LocalDocuments.Clear();
        foreach (DocumentSummary document in documents)
        {
            LocalDocuments.Add(document);
        }

        RoutePageViewModel page = _pages[AppRoute.Library];
        page.StateKind = documents.Count == 0 ? "EMPTY" : "LOCAL";
        page.StateTitle = documents.Count == 0
            ? "No local documents"
            : $"{documents.Count:N0} local document{(documents.Count == 1 ? string.Empty : "s")}";
        page.Detail = documents.Count == 0
            ? "Create a document to begin. Network access is not required."
            : string.Join(" · ", documents.Take(4).Select(document => document.Title));
    }

    private async Task OpenDocumentCoreAsync(Guid documentId)
    {
        EnsureDocumentWorkflowConfigured();
        IsDocumentSaving = true;
        DocumentSaveState = "Opening…";
        try
        {
            DocumentSnapshot? snapshot = await _documentLibrary!.OpenAsync(documentId).ConfigureAwait(true);
            if (snapshot is null)
            {
                StatusMessage = "The selected local document no longer exists";
                await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
                if (LocalDocuments.Count == 0)
                {
                    ClearDocumentWorkspace();
                }

                return;
            }

            ApplyDocumentSnapshot(snapshot, $"Opened locally · {snapshot.Title}");
            SelectedNavigationItem = NavigationItems.First(item => item.Route == AppRoute.Studio);
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            IsDocumentSaving = false;
            DocumentSaveState = "Open failed";
            StatusMessage = "The selected local document could not be opened";
        }
    }

    private void ApplyDocumentSnapshot(DocumentSnapshot snapshot, string statusMessage)
    {
        _suppressDocumentChangeTracking = true;
        try
        {
            DocumentTitle = snapshot.Title;
            DocumentText = snapshot.Text;
        }
        finally
        {
            _suppressDocumentChangeTracking = false;
        }

        _currentDocumentId = snapshot.Id;
        ApplySaveMetadata(snapshot);
        IsDocumentDirty = false;
        IsDocumentSaving = false;
        HasDocumentConflict = false;
        DocumentSaveState = "Saved locally";
        LifecycleState = WorkspaceLifecycleState.Ready;
        StatusMessage = statusMessage;
        NotifyDocumentIdentityChanged();
    }

    private void ClearDocumentWorkspace()
    {
        _suppressDocumentChangeTracking = true;
        try
        {
            DocumentTitle = string.Empty;
            DocumentText = string.Empty;
        }
        finally
        {
            _suppressDocumentChangeTracking = false;
        }

        _currentDocumentId = null;
        _currentRevisionId = null;
        _currentDocumentConcurrencyVersion = 0;
        IsDocumentDirty = false;
        IsDocumentSaving = false;
        HasDocumentConflict = false;
        DocumentSaveState = "No document selected";
        LifecycleState = WorkspaceLifecycleState.Empty;
        StatusMessage = "Local library is empty · create a document to begin";
        NotifyDocumentIdentityChanged();
    }

    private void NotifyDocumentIdentityChanged()
    {
        OnPropertyChanged(nameof(HasOpenDocument));
        OnPropertyChanged(nameof(RequiresDocumentSaveBeforeClose));
        OnPropertyChanged(nameof(CurrentDocumentId));
        OnPropertyChanged(nameof(CurrentRevisionId));
    }

    private void EnsureDocumentWorkflowConfigured()
    {
        if (_documentLibrary is null || _documentAutosave is null)
        {
            throw new InvalidOperationException("The Stage 3 document workflow has not been configured.");
        }
    }
}
