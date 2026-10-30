using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedLocalCreateRetainsSafeStageEvenWhenLoggerFails(int loggerFailure)
    {
        var logger = new DocumentFailureLogger(loggerFailure);
        var library = new FailingDocumentLibrary();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel(logger);
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        await model.NewDocumentCommand.ExecuteAsync(null);
        Assert.Null(model.CurrentDocumentId);
        Assert.False(model.IsDocumentSaving);
        Assert.Equal(1, library.CreateCalls);
        Assert.Contains("could not be created", model.StatusMessage, StringComparison.Ordinal);
        if (loggerFailure == 0)
        {
            Assert.Contains("DocumentCreate", logger.Text, StringComparison.Ordinal);
            Assert.Contains("Storage", logger.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(FailingDocumentLibrary.Sensitive, logger.Text, StringComparison.Ordinal);
            Assert.Null(logger.Exception);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedCheckpointDoesNotLoseEditedTextOrAllowCloseWhenLoggingFails(int loggerFailure)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = new FailingDocumentLibrary(fixture.NewLibrary());
        var logger = new DocumentFailureLogger(loggerFailure);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel(logger);
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        await model.NewDocumentCommand.ExecuteAsync(null);
        Guid original = Assert.IsType<Guid>(model.CurrentDocumentId);
        model.DocumentText = "synthetic private editor text";
        Assert.False(await model.PrepareDocumentCloseAsync());
        Assert.Equal(original, model.CurrentDocumentId);
        Assert.Equal("synthetic private editor text", model.DocumentText);
        Assert.True(model.IsDocumentDirty);
        Assert.True(model.RequiresDocumentSaveBeforeClose);
        if (loggerFailure == 0)
        {
            Assert.Contains("DocumentSave", logger.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(FailingDocumentLibrary.Sensitive, logger.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(model.DocumentText, logger.Text, StringComparison.Ordinal);
            Assert.Null(logger.Exception);
        }
    }

    [Fact]
    public async Task LibraryFailureAfterCheckpointIsRecordedAsPostSaveAndDoesNotEraseCommittedText()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = new FailingDocumentLibrary(fixture.NewLibrary(), failAfterSave: true);
        var logger = new DocumentFailureLogger(0);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel(logger);
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        await model.NewDocumentCommand.ExecuteAsync(null);
        Guid original = Assert.IsType<Guid>(model.CurrentDocumentId);
        model.DocumentText = "synthetic saved before library read failed";
        Assert.False(await model.PrepareDocumentCloseAsync());
        Assert.Equal(model.DocumentText, (await fixture.NewLibrary().OpenAsync(original, TestContext.Current.CancellationToken))!.Text);
        Assert.Contains("DocumentPostSavePresentation", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(model.DocumentText, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(FailingDocumentLibrary.Sensitive, logger.Text, StringComparison.Ordinal);
    }

    private sealed class FailingDocumentLibrary(IDocumentLibrary? inner = null, bool failAfterSave = false) : IDocumentLibrary
    {
        private bool _saved;
        internal const string Sensitive = "fake-private-key article-fragment C:\\private\\document.txt";
        public int CreateCalls { get; private set; }
        public Task<DocumentSnapshot> CreateAsync(string title, string text, CancellationToken token = default)
        {
            CreateCalls++;
            return inner?.CreateAsync(title, text, token) ?? Task.FromException<DocumentSnapshot>(new IOException(Sensitive));
        }
        public Task<DocumentSnapshot?> OpenAsync(Guid id, CancellationToken token = default) => inner?.OpenAsync(id, token) ?? Task.FromResult<DocumentSnapshot?>(null);
        public Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentStatus status = DocumentStatus.Active, int limit = 100, CancellationToken token = default) => _saved ? Task.FromException<IReadOnlyList<DocumentSummary>>(new IOException(Sensitive)) : inner?.ListAsync(status, limit, token) ?? Task.FromResult<IReadOnlyList<DocumentSummary>>([]);
        public Task<IReadOnlyList<DocumentSummary>> SearchAsync(string query, DocumentStatus status = DocumentStatus.Active, int limit = 50, CancellationToken token = default) => inner?.SearchAsync(query, status, limit, token) ?? Task.FromResult<IReadOnlyList<DocumentSummary>>([]);
        public async Task<DocumentSnapshot> SaveAsync(DocumentSaveRequest request, CancellationToken token = default)
        {
            if (!failAfterSave || inner is null) throw new IOException(Sensitive);
            var result = await inner.SaveAsync(request, token).ConfigureAwait(false);
            _saved = true;
            return result;
        }
        public Task<DocumentSummary> ChangeStatusAsync(Guid id, DocumentStatus status, long version, CancellationToken token = default) => throw new InvalidOperationException("Not configured in synthetic failure fixture.");
    }

    private sealed class DocumentFailureLogger(int failure) : ILogger<ShellViewModel>
    {
        public string Text { get; private set; } = string.Empty;
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => failure == 1 ? throw new IOException("synthetic logger failure") : true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (failure == 2) throw new IOException("synthetic logger failure");
            Text += formatter(state, error);
            Exception = error;
        }
    }
}
