using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;

namespace CloudScribe.Application.Tests;

public sealed class DocumentAutosaveCoordinatorTests
{
    [Fact]
    public async Task RapidMutationsCoalesceToLatestAutosave()
    {
        RecordingDocumentLibrary library = new();
        await using DocumentAutosaveCoordinator coordinator = new(library, TimeProvider.System);
        Guid documentId = Guid.NewGuid();
        DocumentSaveRequest first = new(
            documentId,
            "Draft",
            "first",
            1,
            DocumentRevisionKind.Checkpoint);
        DocumentSaveRequest second = first with { Text = "second" };

        Task<DocumentSnapshot?> firstTask = coordinator.QueueAsync(
            first,
            TimeSpan.FromMilliseconds(150),
            TestContext.Current.CancellationToken);
        Task<DocumentSnapshot?> secondTask = coordinator.QueueAsync(
            second,
            TimeSpan.FromMilliseconds(30),
            TestContext.Current.CancellationToken);

        Assert.Null(await firstTask);
        DocumentSnapshot? saved = await secondTask;
        Assert.NotNull(saved);
        Assert.Equal("second", saved.Text);
        Assert.Single(library.Saves);
        Assert.Equal(DocumentRevisionKind.Autosave, library.Saves[0].RevisionKind);
    }

    [Fact]
    public async Task ExplicitCheckpointCancelsPendingAutosaveAndUsesCheckpointKind()
    {
        RecordingDocumentLibrary library = new();
        await using DocumentAutosaveCoordinator coordinator = new(library, TimeProvider.System);
        Guid documentId = Guid.NewGuid();
        DocumentSaveRequest pending = new(
            documentId,
            "Draft",
            "pending",
            1,
            DocumentRevisionKind.Autosave);
        _ = coordinator.QueueAsync(
            pending,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        DocumentSnapshot checkpoint = await coordinator.SaveCheckpointAsync(
            pending with { Text = "manual" },
            TestContext.Current.CancellationToken);

        Assert.Equal("manual", checkpoint.Text);
        Assert.Single(library.Saves);
        Assert.Equal(DocumentRevisionKind.Checkpoint, library.Saves[0].RevisionKind);
    }

    private sealed class RecordingDocumentLibrary : IDocumentLibrary
    {
        public List<DocumentSaveRequest> Saves { get; } = [];

        public Task<DocumentSnapshot> CreateAsync(string title, string text, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DocumentSnapshot?> OpenAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DocumentSummary>> ListAsync(
            DocumentStatus status = DocumentStatus.Active,
            int limit = 100,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DocumentSummary>> SearchAsync(
            string query,
            DocumentStatus status = DocumentStatus.Active,
            int limit = 50,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DocumentSnapshot> SaveAsync(
            DocumentSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saves.Add(request);
            return Task.FromResult(new DocumentSnapshot(
                request.DocumentId,
                request.Title,
                request.Text,
                1,
                2,
                DocumentStatus.Active,
                false,
                Guid.NewGuid(),
                null,
                null,
                request.ExpectedConcurrencyVersion + 1));
        }

        public Task<DocumentSummary> ChangeStatusAsync(
            Guid documentId,
            DocumentStatus status,
            long expectedConcurrencyVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
