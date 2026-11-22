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

    [Fact]
    public async Task FaultedPendingAutosaveDoesNotBlockCheckpointOrDispose()
    {
        RecordingDocumentLibrary library = new() { FailFirstSave = true };
        DocumentAutosaveCoordinator coordinator = new(library, TimeProvider.System);
        Guid documentId = Guid.NewGuid();
        DocumentSaveRequest request = new(
            documentId,
            "Draft",
            "retryable",
            1,
            DocumentRevisionKind.Autosave);

        Task<DocumentSnapshot?> failedAutosave = coordinator.QueueAsync(
            request,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<IOException>(() => failedAutosave);

        DocumentSnapshot checkpoint = await coordinator.SaveCheckpointAsync(
            request with { Text = "recovered checkpoint" },
            TestContext.Current.CancellationToken);

        Assert.Equal("recovered checkpoint", checkpoint.Text);
        Assert.Equal(DocumentRevisionKind.Checkpoint, library.Saves[^1].RevisionKind);

        library.FailFirstSave = true;
        Task<DocumentSnapshot?> trailingFailure = coordinator.QueueAsync(
            request with { ExpectedConcurrencyVersion = checkpoint.ConcurrencyVersion },
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<IOException>(() => trailingFailure);

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task CompletedInFlightAutosaveAdvancesCheckpointConcurrencyVersion()
    {
        RecordingDocumentLibrary library = new() { EnforceStrictConcurrency = true };
        await using DocumentAutosaveCoordinator coordinator = new(library, TimeProvider.System);
        Guid documentId = Guid.NewGuid();
        DocumentSaveRequest request = new(
            documentId,
            "Draft",
            "autosaved first",
            1,
            DocumentRevisionKind.Autosave);

        Task<DocumentSnapshot?> inFlight = coordinator.QueueAsync(
            request,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        DocumentSnapshot? autosaved = await inFlight;
        Assert.NotNull(autosaved);
        Assert.Equal(2, autosaved.ConcurrencyVersion);

        // Caller still holds the pre-autosave concurrency version (1) when requesting a checkpoint.
        DocumentSnapshot checkpoint = await coordinator.SaveCheckpointAsync(
            request with { Text = "checkpoint after autosave", ExpectedConcurrencyVersion = 1 },
            TestContext.Current.CancellationToken);

        Assert.Equal("checkpoint after autosave", checkpoint.Text);
        Assert.Equal(3, checkpoint.ConcurrencyVersion);
        Assert.Equal(2, library.Saves.Count);
        Assert.Equal(2, library.Saves[1].ExpectedConcurrencyVersion);
    }

    private sealed class RecordingDocumentLibrary : IDocumentLibrary
    {
        private long _currentVersion = 1;

        public bool FailFirstSave { get; set; }

        public bool EnforceStrictConcurrency { get; set; }

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
            if (FailFirstSave)
            {
                FailFirstSave = false;
                throw new IOException("Simulated transient disk failure");
            }

            if (EnforceStrictConcurrency && request.ExpectedConcurrencyVersion != _currentVersion)
            {
                throw new DocumentConcurrencyException(request.DocumentId, request.ExpectedConcurrencyVersion);
            }

            Saves.Add(request);
            _currentVersion = request.ExpectedConcurrencyVersion + 1;
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
                _currentVersion));
        }

        public Task<DocumentSummary> ChangeStatusAsync(
            Guid documentId,
            DocumentStatus status,
            long expectedConcurrencyVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
