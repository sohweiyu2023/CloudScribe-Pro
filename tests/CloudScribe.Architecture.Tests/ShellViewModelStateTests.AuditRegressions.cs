using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task MissingDocumentOpenDuringActiveSearchFilterDoesNotClearWorkspace()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        var existing = await library.CreateAsync("Retained article", "Retained body text", TestContext.Current.CancellationToken);
        await model.OpenDocumentCommand.ExecuteAsync(existing.Id);

        model.LibrarySearchQuery = "nonexistent-filter-term";
        await model.RefreshDocumentLibraryCommand.ExecuteAsync(null);
        Assert.Empty(model.LocalDocuments);

        await model.OpenDocumentCommand.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(existing.Id, model.CurrentDocumentId);
        Assert.Equal("Retained body text", model.DocumentText);
        Assert.Equal("The selected local document no longer exists", model.StatusMessage);
    }

    [Fact]
    public async Task ClearLibrarySearchCommandResetsQueryAndState()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);

        Assert.False(model.HasLibrarySearchQuery);
        model.LibrarySearchQuery = "meditation";
        Assert.True(model.HasLibrarySearchQuery);

        model.ClearLibrarySearchCommand.Execute(null);

        Assert.Equal(string.Empty, model.LibrarySearchQuery);
        Assert.False(model.HasLibrarySearchQuery);
    }

    [Fact]
    public void LibraryArticleAndPartExposeDescriptiveOpenAccessibleNames()
    {
        var articleDoc = new DocumentSummary(Guid.NewGuid(), "Dharma Essay", 100, DocumentStatus.Active, false, 1);
        var partDoc = new DocumentSummary(Guid.NewGuid(), "Dharma Essay - Part 1", 101, DocumentStatus.Active, false, 1);
        var part = new NarrationLibraryPart(partDoc, 1, false);
        var article = new NarrationLibraryArticle(articleDoc, [part], false);
        using var model = CreateViewModel();

        var articleVm = new LibraryArticleViewModel(article, model.OpenDocumentCommand);

        Assert.Equal("Open article: Dharma Essay", articleVm.OpenAccessibleName);
        Assert.Single(articleVm.Parts);
        Assert.Equal("Open part: Dharma Essay - Part 1", articleVm.Parts[0].OpenAccessibleName);
    }
}
