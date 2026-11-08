using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CloudScribe.Application.Documents;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public Task NarrationPreviewSaveFailureAllowsEscapeAndKeepsSourceAndFocus() => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        var library = f.Disk.NewLibrary();
        const string original = "Save failure https://example.org/path keeps this source.";
        var document = await library.CreateAsync("Synthetic failure", original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await model.OpenDocumentCommand.ExecuteAsync(document.Id).ConfigureAwait(true);
        var copies = new FailingPreviewCopies();
        model.ConfigureNarrationCopies(copies);
        using var ui = new CatalogComparisonUi(f, model);
        Task<bool> closed = OpenNarrationPreview(ui.Owner, model);
        Window dialog = ui.Dialog;
        CatalogButton(dialog, "Save and use narration copy").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettlePreviewFocusAsync(dialog, text: true).ConfigureAwait(true);
        Assert.False(closed.IsCompleted);
        Assert.True(CatalogButton(dialog, "Close preview").IsEnabled);
        Assert.Contains(dialog.GetLogicalDescendants().OfType<TextBlock>(), item =>
            item.Text?.Contains("Synthetic save failure", StringComparison.Ordinal) == true);
        int focuses = 0;
        ui.Invoker.GotFocus += (_, _) => focuses++;
        DismissNarrationPreview(dialog, "escape");
        Assert.False(await closed.ConfigureAwait(true));
        Assert.Equal(1, copies.Attempts);
        Assert.Equal(1, focuses);
        Assert.Same(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
        Assert.Equal(document.Id, model.CurrentDocumentId);
        Assert.Equal(document.CurrentRevisionId, model.CurrentRevisionId);
        Assert.Equal(original, model.DocumentText);
        Assert.Equal(document, await library.OpenAsync(document.Id, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NarrationPreviewDoesNotRestoreDisabledOrDetachedInvoker(bool detached) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        model.DocumentText = "Synthetic preview.";
        using var ui = new CatalogComparisonUi(f, model);
        Task<bool> closed = OpenNarrationPreview(ui.Owner, model);
        Window dialog = ui.Dialog;
        await SettlePreviewFocusAsync(dialog, text: true).ConfigureAwait(true);
        if (detached) Assert.True(Assert.IsAssignableFrom<Panel>(ui.Invoker.Parent).Children.Remove(ui.Invoker));
        else ui.Invoker.IsEnabled = false;
        int focuses = 0;
        ui.Invoker.GotFocus += (_, _) => focuses++;
        DismissNarrationPreview(dialog, "escape");
        Assert.False(await closed.ConfigureAwait(true));
        Assert.Equal(0, focuses);
        Assert.False(ui.Invoker.IsFocused);
        Assert.NotSame(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
        Assert.True(ui.Owner.IsVisible);
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    private sealed class FailingPreviewCopies : INarrationCopyStore
    {
        internal int Attempts { get; private set; }
        public Task<NarrationCopyPreparation> PrepareAsync(Guid sourceDocumentId, Guid sourceRevisionId, string sourceTextSha256,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Task.FromException<NarrationCopyPreparation>(new IOException("Synthetic save failure."));
        }
        public Task<NarrationCopyPreparation> PrepareAsync(Guid sourceDocumentId, Guid sourceRevisionId, string sourceTextSha256,
            string transformVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NarrationCopyPreparation?> FindAsync(Guid narrationDocumentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentSnapshot> ImportBatchSourceAsync(Guid batchId, Guid itemId, string rawSha256, string title,
            string normalizedText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
