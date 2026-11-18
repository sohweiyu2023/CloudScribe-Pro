using CloudScribe.App.Design;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData("open-error", WorkspaceLifecycleState.Error)]
    [InlineData("missing-retained", WorkspaceLifecycleState.Error)]
    [InlineData("missing-empty", WorkspaceLifecycleState.Empty)]
    [InlineData("success", WorkspaceLifecycleState.Ready)]
    [InlineData("list-error", WorkspaceLifecycleState.Error)]
    public async Task StartupDocumentFailureLeavesLoadingAndAllowsExplicitRecovery(string scenario, WorkspaceLifecycleState expected)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var disk = fixture.NewLibrary();
        var snapshot = await disk.CreateAsync("Synthetic startup article", "Synthetic startup body", TestContext.Current.CancellationToken);
        var summaries = await disk.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
        if (string.Equals(scenario, "missing-retained", StringComparison.Ordinal))
        {
            var retained = await disk.CreateAsync("Synthetic remaining article", "Synthetic remaining body", TestContext.Current.CancellationToken);
            var all = await disk.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
            summaries = [summaries.Single(), all.Single(item => item.Id == retained.Id)];
        }
        var library = new StartupLifecycleLibrary(snapshot, summaries, scenario);
        var logger = new DocumentFailureLogger(0);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel(logger);
        model.ConfigureStage3DocumentWorkflow(library, autosave);

        // Every scripted operation returns a completed task: startup reaches its terminal path before this call returns.
        model.StartDocumentWorkspace();

        Assert.Equal(expected, model.LifecycleState);
        Assert.False(model.IsLoadingWorkspaceVisible);
        Assert.False(model.IsDocumentSaving);
        Assert.Equal(0, library.WriteCalls);
        Assert.DoesNotContain(StartupLifecycleLibrary.Sensitive, logger.Text, StringComparison.Ordinal);
        if (string.Equals(scenario, "open-error", StringComparison.Ordinal))
        {
            Assert.Equal("The selected local document could not be opened", model.StatusMessage);
            Assert.Contains("DocumentOpen", logger.Text, StringComparison.Ordinal);
        }
        if (string.Equals(scenario, "missing-retained", StringComparison.Ordinal))
        {
            Assert.Equal("The selected local document no longer exists", model.StatusMessage);
            Assert.Single(model.LocalDocuments);
            Assert.DoesNotContain(model.LocalDocuments, item => item.Id == snapshot.Id);
        }
        Assert.Equal(snapshot.Text, (await disk.OpenAsync(snapshot.Id, TestContext.Current.CancellationToken))!.Text);

        library.Recover = true;
        await model.OpenDocumentCommand.ExecuteAsync(snapshot.Id);
        Assert.Equal(WorkspaceLifecycleState.Ready, model.LifecycleState);
        Assert.Equal(snapshot.Id, model.CurrentDocumentId);
        Assert.Equal(snapshot.Text, model.DocumentText);
        Assert.Equal(0, library.WriteCalls);
    }

    [Fact]
    public async Task StartupLifecycleFixDoesNotChangeFailedSelectionOfExistingReadyDocument()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var disk = fixture.NewLibrary();
        var snapshot = await disk.CreateAsync("Synthetic retained article", "Synthetic retained body", TestContext.Current.CancellationToken);
        var library = new StartupLifecycleLibrary(snapshot, await disk.ListAsync(cancellationToken: TestContext.Current.CancellationToken), "open-error") { Recover = true };
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        model.StartDocumentWorkspace();
        Assert.Equal(WorkspaceLifecycleState.Ready, model.LifecycleState);

        library.Recover = false;
        await model.OpenDocumentCommand.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(WorkspaceLifecycleState.Ready, model.LifecycleState);
        Assert.Equal(snapshot.Id, model.CurrentDocumentId);
        Assert.Equal(snapshot.Text, model.DocumentText);
        Assert.False(model.IsDocumentSaving);
        Assert.Equal(0, library.WriteCalls);
    }

    private sealed class StartupLifecycleLibrary(DocumentSnapshot snapshot, IReadOnlyList<DocumentSummary> summaries, string scenario) : IDocumentLibrary
    {
        internal const string Sensitive = "synthetic-private-content C:\\private\\startup.txt";
        private int _reads;
        public bool Recover { get; set; }
        public int WriteCalls { get; private set; }
        public Task<DocumentSnapshot?> OpenAsync(Guid id, CancellationToken cancellationToken = default) =>
            Recover || string.Equals(scenario, "success", StringComparison.Ordinal) ? Task.FromResult<DocumentSnapshot?>(snapshot)
            : string.Equals(scenario, "open-error", StringComparison.Ordinal) ? Task.FromException<DocumentSnapshot?>(new IOException(Sensitive))
            : Task.FromResult<DocumentSnapshot?>(null);
        public Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentStatus status = DocumentStatus.Active, int limit = 100, CancellationToken cancellationToken = default)
        {
            _reads++;
            if (!Recover && string.Equals(scenario, "list-error", StringComparison.Ordinal)) return Task.FromException<IReadOnlyList<DocumentSummary>>(new IOException(Sensitive));
            if (!Recover && string.Equals(scenario, "missing-retained", StringComparison.Ordinal) && _reads > 1)
                return Task.FromResult<IReadOnlyList<DocumentSummary>>(summaries.Skip(1).ToArray());
            return Task.FromResult<IReadOnlyList<DocumentSummary>>(!Recover && string.Equals(scenario, "missing-empty", StringComparison.Ordinal) && _reads > 1 ? [] : summaries);
        }
        public Task<IReadOnlyList<DocumentSummary>> SearchAsync(string query, DocumentStatus status = DocumentStatus.Active, int limit = 50, CancellationToken cancellationToken = default) => ListAsync(status, limit, cancellationToken);
        public Task<DocumentSnapshot> CreateAsync(string title, string text, CancellationToken cancellationToken = default) { WriteCalls++; throw new InvalidOperationException("Unexpected synthetic write."); }
        public Task<DocumentSnapshot> SaveAsync(DocumentSaveRequest request, CancellationToken cancellationToken = default) { WriteCalls++; throw new InvalidOperationException("Unexpected synthetic write."); }
        public Task<DocumentSummary> ChangeStatusAsync(Guid id, DocumentStatus status, long version, CancellationToken cancellationToken = default) { WriteCalls++; throw new InvalidOperationException("Unexpected synthetic write."); }
    }
}
