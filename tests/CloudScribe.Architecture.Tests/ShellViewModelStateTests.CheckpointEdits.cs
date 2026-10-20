using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task NormalizedTitleDoesNotLeaveDocumentPermanentlyDirty()
    {
        var library = new DelayedCheckpointLibrary(); library.Continue.SetResult();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using ShellViewModel model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(library, autosave);
        await model.NewDocumentCommand.ExecuteAsync(null);
        model.DocumentTitle = "  Narration title  ";
        Assert.True(await model.PrepareDocumentCloseAsync());
        Assert.False(model.IsDocumentDirty);
    }

    [Fact]
    public async Task NewerEditsDuringCloseCheckpointRemainDirtyAndPreventClose()
    {
        var library = new DelayedCheckpointLibrary();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using ShellViewModel model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        await model.NewDocumentCommand.ExecuteAsync(null);
        model.DocumentText = "First version";
        Task<bool> closing = model.PrepareDocumentCloseAsync();
        await library.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        model.DocumentText = "Newer text typed while saving";
        library.Continue.SetResult();
        Assert.False(await closing);
        Assert.True(model.IsDocumentDirty);
        Assert.Equal("Newer text typed while saving", model.DocumentText);
    }

    private sealed class DelayedCheckpointLibrary : IDocumentLibrary
    {
        private DocumentSnapshot _snapshot = new(Guid.NewGuid(), "Untitled", string.Empty, 0, 0, DocumentStatus.Active, false, null, null, null, 0);
        public TaskCompletionSource Entered { get; } = new();
        public TaskCompletionSource Continue { get; } = new();
        public Task<DocumentSnapshot> CreateAsync(string title, string text, CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);
        public Task<DocumentSnapshot?> OpenAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.FromResult<DocumentSnapshot?>(_snapshot);
        public Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentStatus status = DocumentStatus.Active, int limit = 100, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DocumentSummary>>([]);
        public Task<IReadOnlyList<DocumentSummary>> SearchAsync(string query, DocumentStatus status = DocumentStatus.Active, int limit = 50, CancellationToken cancellationToken = default) => ListAsync(status, limit, cancellationToken);
        public Task<DocumentSummary> ChangeStatusAsync(Guid documentId, DocumentStatus status, long expectedConcurrencyVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<DocumentSnapshot> SaveAsync(DocumentSaveRequest request, CancellationToken cancellationToken = default)
        {
            if (request.RevisionKind == DocumentRevisionKind.Checkpoint)
            {
                Entered.SetResult();
                await Continue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            _snapshot = _snapshot with { Text = request.Text, Title = request.Title, CurrentRevisionId = Guid.NewGuid(), ConcurrencyVersion = _snapshot.ConcurrencyVersion + 1 };
            return _snapshot;
        }
    }
}
