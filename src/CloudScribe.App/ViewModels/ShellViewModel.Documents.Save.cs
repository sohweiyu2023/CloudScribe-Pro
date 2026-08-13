using System.ComponentModel;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private static readonly TimeSpan DocumentAutosaveDebounce = TimeSpan.FromMilliseconds(750);

    public async Task<bool> PrepareDocumentCloseAsync()
    {
        EnsureDocumentWorkflowConfigured();
        if (!RequiresDocumentSaveBeforeClose)
        {
            return true;
        }

        return await SaveCurrentDocumentCoreAsync("Close checkpoint").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveDocumentAsync() =>
        _ = await SaveCurrentDocumentCoreAsync("Manual checkpoint").ConfigureAwait(true);

    private async Task<bool> SaveCurrentDocumentCoreAsync(string revisionName)
    {
        EnsureDocumentWorkflowConfigured();
        if (_currentDocumentId is not Guid documentId)
        {
            return true;
        }

        IsDocumentSaving = true;
        DocumentSaveState = "Saving checkpoint…";
        DocumentSaveRequest request = BuildSaveRequest(
            documentId,
            DocumentRevisionKind.Checkpoint,
            revisionName);
        try
        {
            DocumentSnapshot snapshot = await _documentAutosave!
                .SaveCheckpointAsync(request)
                .ConfigureAwait(true);
            ApplySaveMetadata(snapshot);
            IsDocumentDirty = false;
            HasDocumentConflict = false;
            DocumentSaveState = "Saved locally";
            StatusMessage = "Saved local checkpoint";
            await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
            return true;
        }
        catch (DocumentConcurrencyException)
        {
            IsDocumentSaving = false;
            IsDocumentDirty = true;
            HasDocumentConflict = true;
            DocumentSaveState = "Conflict — reopen required";
            StatusMessage = "A newer local revision exists · your text was not overwritten";
            return false;
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            IsDocumentSaving = false;
            IsDocumentDirty = true;
            DocumentSaveState = "Save failed — retry required";
            StatusMessage = "Local save failed · the editor remains open with your current text";
            return false;
        }
    }

    private void OnDocumentWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_suppressDocumentChangeTracking
            || _documentAutosave is null
            || _currentDocumentId is null
            || eventArgs.PropertyName is not (nameof(DocumentTitle) or nameof(DocumentText)))
        {
            return;
        }

        IsDocumentDirty = true;
        HasDocumentConflict = false;
        DocumentSaveState = "Autosave pending…";
        OnPropertyChanged(nameof(RequiresDocumentSaveBeforeClose));
        QueueDocumentAutosave();
    }

    private void QueueDocumentAutosave()
    {
        if (_currentDocumentId is not Guid documentId || _documentAutosave is null)
        {
            return;
        }

        long sequence = Interlocked.Increment(ref _autosaveSequence);
        IsDocumentSaving = true;
        DocumentSaveRequest request = BuildSaveRequest(
            documentId,
            DocumentRevisionKind.Autosave,
            revisionName: null);
        Task<DocumentSnapshot?> pending = _documentAutosave.QueueAsync(request, DocumentAutosaveDebounce);
        _ = ObserveDocumentAutosaveAsync(sequence, request, pending);
    }

    private async Task ObserveDocumentAutosaveAsync(
        long sequence,
        DocumentSaveRequest request,
        Task<DocumentSnapshot?> pending)
    {
        try
        {
            DocumentSnapshot? snapshot = await pending.ConfigureAwait(true);
            if (snapshot is null || sequence != Volatile.Read(ref _autosaveSequence))
            {
                return;
            }

            ApplySaveMetadata(snapshot);
            bool stillMatches = string.Equals(DocumentTitle, request.Title, StringComparison.Ordinal)
                && string.Equals(DocumentText, request.Text, StringComparison.Ordinal);
            IsDocumentDirty = !stillMatches;
            IsDocumentSaving = false;
            HasDocumentConflict = false;
            DocumentSaveState = stillMatches ? "Autosaved locally" : "Autosave pending…";
            OnPropertyChanged(nameof(RequiresDocumentSaveBeforeClose));
            if (!stillMatches)
            {
                QueueDocumentAutosave();
            }
        }
        catch (DocumentConcurrencyException)
        {
            if (sequence != Volatile.Read(ref _autosaveSequence))
            {
                return;
            }

            IsDocumentSaving = false;
            IsDocumentDirty = true;
            HasDocumentConflict = true;
            DocumentSaveState = "Conflict — reopen required";
            StatusMessage = "Autosave detected a newer local revision · no overwrite occurred";
            OnPropertyChanged(nameof(RequiresDocumentSaveBeforeClose));
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            if (sequence != Volatile.Read(ref _autosaveSequence))
            {
                return;
            }

            IsDocumentSaving = false;
            IsDocumentDirty = true;
            DocumentSaveState = "Autosave failed — retry required";
            StatusMessage = "Autosave failed · your current editor text remains in memory";
            OnPropertyChanged(nameof(RequiresDocumentSaveBeforeClose));
        }
    }

    private DocumentSaveRequest BuildSaveRequest(
        Guid documentId,
        DocumentRevisionKind revisionKind,
        string? revisionName) => new(
            documentId,
            EffectiveDocumentTitle(),
            DocumentText,
            _currentDocumentConcurrencyVersion,
            revisionKind,
            revisionName);

    private string EffectiveDocumentTitle() =>
        string.IsNullOrWhiteSpace(DocumentTitle) ? "Untitled document" : DocumentTitle.Trim();

    private void ApplySaveMetadata(DocumentSnapshot snapshot)
    {
        _currentDocumentConcurrencyVersion = snapshot.ConcurrencyVersion;
        _currentRevisionId = snapshot.CurrentRevisionId;
        IsDocumentSaving = false;
        OnPropertyChanged(nameof(CurrentRevisionId));
    }
}
