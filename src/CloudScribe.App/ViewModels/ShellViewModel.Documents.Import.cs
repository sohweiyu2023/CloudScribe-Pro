using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private ILocalDocumentImporter? _localDocumentImporter;
    private DocumentPreprocessor? _documentPreprocessor;

    public event EventHandler? ImportDocumentRequested;

    public void ConfigureStage3ImportWorkflow(
        ILocalDocumentImporter localDocumentImporter,
        DocumentPreprocessor documentPreprocessor)
    {
        ArgumentNullException.ThrowIfNull(localDocumentImporter);
        ArgumentNullException.ThrowIfNull(documentPreprocessor);
        if (_localDocumentImporter is not null || _documentPreprocessor is not null)
        {
            throw new InvalidOperationException("The Stage 3 import workflow is already configured.");
        }

        _localDocumentImporter = localDocumentImporter;
        _documentPreprocessor = documentPreprocessor;
    }

    [RelayCommand]
    private void ImportDocument() => ImportDocumentRequested?.Invoke(this, EventArgs.Empty);

    public async Task ImportDocumentAsync(
        LocalDocumentImportKind kind,
        string displayName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        EnsureDocumentWorkflowConfigured();
        EnsureImportWorkflowConfigured();
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(content);

        if (RequiresDocumentSaveBeforeClose
            && !await SaveCurrentDocumentCoreAsync("Pre-import checkpoint").ConfigureAwait(true))
        {
            return;
        }

        IsDocumentSaving = true;
        DocumentSaveState = "Importing safely…";
        try
        {
            LocalDocumentImportResult imported = await _localDocumentImporter!
                .ImportAsync(new(kind, displayName, content), cancellationToken)
                .ConfigureAwait(true);
            DocumentPreprocessingPreview preview = _documentPreprocessor!.Preview(
                imported.Text,
                new(
                    NormalizeLineEndings: true,
                    CollapseHorizontalWhitespace: false,
                    CollapseExcessBlankLines: false,
                    SimplifyUrls: false));

            DocumentSnapshot created = await _documentLibrary!
                .CreateAsync(imported.SuggestedTitle, preview.OutputText, cancellationToken)
                .ConfigureAwait(true);
            DocumentSnapshot provenanceRevision = await _documentLibrary
                .SaveAsync(
                    new(
                        created.Id,
                        created.Title,
                        preview.OutputText,
                        created.ConcurrencyVersion,
                        DocumentRevisionKind.Import,
                        "Imported source",
                        imported.Provenance),
                    cancellationToken)
                .ConfigureAwait(true);

            ApplyDocumentSnapshot(provenanceRevision, $"Imported locally · {displayName}");
            DocumentSaveState = imported.Warnings.Count == 0
                ? "Imported and saved locally"
                : $"Imported safely · {imported.Warnings.Count} note{(imported.Warnings.Count == 1 ? string.Empty : "s")}";
            await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            IsDocumentSaving = false;
            DocumentSaveState = "Import failed safely";
            StatusMessage = "Import failed · no existing document was overwritten";
        }
    }

    private void EnsureImportWorkflowConfigured()
    {
        if (_localDocumentImporter is null || _documentPreprocessor is null)
        {
            throw new InvalidOperationException("The Stage 3 import workflow has not been configured.");
        }
    }
}
