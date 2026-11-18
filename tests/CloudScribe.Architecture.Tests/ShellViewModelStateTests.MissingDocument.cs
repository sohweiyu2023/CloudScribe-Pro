using CloudScribe.Application.Documents;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task MissingDocumentWithEmptyLibraryKeepsExistingEmptyWorkspaceBehavior()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);

        await model.OpenDocumentCommand.ExecuteAsync(Guid.NewGuid());

        Assert.False(model.IsDocumentSaving);
        Assert.Null(model.CurrentDocumentId);
        Assert.Empty(model.LocalDocuments);
        Assert.Equal("No document selected", model.DocumentSaveState);
    }

    [Fact]
    public async Task MissingDocumentRefreshFailureRetainsExistingSafeErrorHandling()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = new FailingDocumentLibrary(fixture.NewLibrary()) { FailReads = true };
        var logger = new DocumentFailureLogger(0);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel(logger);
        model.ConfigureStage3DocumentWorkflow(library, autosave);

        await model.OpenDocumentCommand.ExecuteAsync(Guid.NewGuid());

        Assert.False(model.IsDocumentSaving);
        Assert.Equal("Open failed", model.DocumentSaveState);
        Assert.Equal("The selected local document could not be opened", model.StatusMessage);
        Assert.Contains("DocumentOpen", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(FailingDocumentLibrary.Sensitive, logger.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingDocumentWithRemainingLibraryLeavesOpeningStateAndAllowsRecovery(bool openCurrent, bool dirtyCurrent)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        var existing = await library.CreateAsync("Synthetic retained article", "Synthetic retained body", TestContext.Current.CancellationToken);
        if (openCurrent)
        {
            await model.OpenDocumentCommand.ExecuteAsync(existing.Id);
            if (dirtyCurrent) model.DocumentText = "Synthetic checkpoint before missing selection";
        }
        Guid? originalId = model.CurrentDocumentId;
        string originalText = model.DocumentText;
        string originalTitle = model.DocumentTitle;

        await model.OpenDocumentCommand.ExecuteAsync(Guid.NewGuid());

        Assert.False(model.IsDocumentSaving);
        Assert.Equal("Open failed", model.DocumentSaveState);
        Assert.Equal("The selected local document no longer exists", model.StatusMessage);
        Assert.Equal(originalId, model.CurrentDocumentId);
        Assert.Equal(originalText, model.DocumentText);
        Assert.Equal(originalTitle, model.DocumentTitle);
        Assert.Contains(model.LocalDocuments, d => d.Id == existing.Id);
        if (dirtyCurrent)
        {
            Assert.False(model.IsDocumentDirty);
            Assert.Equal(originalText, (await library.OpenAsync(existing.Id, TestContext.Current.CancellationToken))!.Text);
        }

        await model.OpenDocumentCommand.ExecuteAsync(existing.Id);

        Assert.False(model.IsDocumentSaving);
        Assert.Equal("Saved locally", model.DocumentSaveState);
        Assert.Equal(existing.Id, model.CurrentDocumentId);
        Assert.Equal(dirtyCurrent ? originalText : existing.Text, model.DocumentText);
    }
}
