using System.IO.Compression;
using System.Text;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task MalformedDocxExplainsInvalidContentAndPreservesOpenDocument()
    {
        var library = new DelayedCheckpointLibrary();
        library.Continue.SetResult();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using ShellViewModel model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        model.ConfigureStage3ImportWorkflow(new BoundedLocalDocumentImporter(), new DocumentPreprocessor());
        await model.NewDocumentCommand.ExecuteAsync(null);
        model.DocumentText = "Keep this local narration.";
        Assert.True(await model.PrepareDocumentCloseAsync());
        Guid? originalDocument = model.CurrentDocumentId;
        Guid? originalRevision = model.CurrentRevisionId;

        using MemoryStream content = new();
        using (ZipArchive archive = new(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("word/document.xml");
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
            writer.Write("<document><unclosed></document>");
        }
        content.Position = 0;

        await model.ImportDocumentAsync(
            LocalDocumentImportKind.Docx, "damaged.docx", content, TestContext.Current.CancellationToken);

        Assert.Equal(originalDocument, model.CurrentDocumentId);
        Assert.Equal(originalRevision, model.CurrentRevisionId);
        Assert.Equal("Keep this local narration.", model.DocumentText);
        Assert.False(model.IsDocumentSaving);
        Assert.Contains("valid supported text", model.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("No existing document was overwritten", model.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("disk access", model.StatusMessage, StringComparison.Ordinal);
    }
}
