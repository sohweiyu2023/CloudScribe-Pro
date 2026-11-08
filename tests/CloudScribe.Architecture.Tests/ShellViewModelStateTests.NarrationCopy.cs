using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task NarrationCopySavesPendingEditorTextAndCapturesItsGenuineRevision()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        var original = await library.CreateAsync("Original", "Earlier saved text.", TestContext.Current.CancellationToken);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        model.ConfigureNarrationCopies(CopyStore(fixture));
        await model.OpenDocumentCommand.ExecuteAsync(original.Id);
        const string edited = "Current text https://example.org/path then continue.";
        model.DocumentText = edited;
        Assert.True(model.IsDocumentDirty);
        var copy = await model.UseNarrationCopyAsync(edited);
        var savedOriginal = (await library.OpenAsync(original.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(edited, savedOriginal.Text);
        Assert.NotEqual(original.CurrentRevisionId, savedOriginal.CurrentRevisionId);
        Assert.Equal(savedOriginal.CurrentRevisionId, copy.SourceRevisionId);
        Assert.Equal(BatchNarrationIdentity.Hash(edited), copy.SourceTextSha256);
        Assert.Equal(copy.Narration.Id, model.CurrentDocumentId);
        Assert.Equal(copy.Narration.CurrentRevisionId, model.CurrentRevisionId);
        Assert.Equal(NarrationUrlOmission.Transform(edited).OutputText, model.DocumentText);
        Assert.False(model.IsDocumentDirty);
        model.SelectedVoiceLabVoice = VoiceSelection() with { LanguageCodes = ["en-US"] };
        var captured = model.CaptureGoogleTtsStudioRequestSelection();
        Assert.Equal(copy.Narration.Id, captured.DocumentId);
        Assert.Equal(copy.Narration.CurrentRevisionId, captured.RevisionId);
        Assert.Equal(copy.Narration.Text, captured.ExactText);
        Assert.Contains("original is unchanged", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewerEditWhileCopyCheckpointSavesPreventsStalePreparation()
    {
        var library = new DelayedCheckpointLibrary();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        var copies = new BlockingCopies(null);
        model.ConfigureNarrationCopies(copies);
        await model.NewDocumentCommand.ExecuteAsync(null);
        const string preview = "Preview https://example.org/path text.";
        model.DocumentText = preview;
        var preparing = model.UseNarrationCopyAsync(preview);
        await library.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        model.DocumentText = "Newer editor text remains.";
        library.Continue.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);
        Assert.Equal(0, copies.Prepares);
        Assert.Equal("Newer editor text remains.", model.DocumentText);
        Assert.True(model.IsDocumentDirty);
    }

    [Fact]
    public async Task CopyPreparationRejectsDuplicateClicksAndNeverSwitchesANewerDocument()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        const string text = "Read https://example.org/path then continue.";
        var original = await library.CreateAsync("First", text, TestContext.Current.CancellationToken);
        var other = await library.CreateAsync("Second", text, TestContext.Current.CancellationToken);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        var copies = new BlockingCopies(CopyStore(fixture)); model.ConfigureNarrationCopies(copies);
        await model.OpenDocumentCommand.ExecuteAsync(original.Id);
        var preparing = model.UseNarrationCopyAsync(text);
        await copies.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.UseNarrationCopyAsync(text));
        await model.OpenDocumentCommand.ExecuteAsync(other.Id);
        copies.Release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);
        Assert.Equal(1, copies.Prepares);
        Assert.Equal(other.Id, model.CurrentDocumentId);
        Assert.Equal(text, model.DocumentText);
        Assert.Equal(text, (await library.OpenAsync(original.Id, TestContext.Current.CancellationToken))!.Text);
    }

    [Fact]
    public async Task SourceEditClearsPreparedCopyApprovalBeforeAnyApprovalDelegateRuns()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        const string text = "Read https://example.org/path then continue.";
        var original = await library.CreateAsync("Original", text, TestContext.Current.CancellationToken);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        model.ConfigureNarrationCopies(CopyStore(fixture));
        await model.OpenDocumentCommand.ExecuteAsync(original.Id);
        await model.UseNarrationCopyAsync(text);
        model.ConfigureStage6GoogleGenerationPreparation(_ => Task.FromResult(new CloudScribe.App.ViewModels.ShellViewModel.GoogleGenerationSpendReview(
            "USD", 2, 1, "pricing", "en-US-Standard-C", new string('a', 64))));
        int approvals = 0;
        model.ConfigureStage6GoogleGenerationSpendApproval((_, _, _) => { approvals++; return Task.CompletedTask; });
        await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(model.PreparedGoogleGenerationSpendReview);
        await library.SaveAsync(new(original.Id, original.Title, "New original text.", original.ConcurrencyVersion,
            CloudScribe.Domain.Documents.DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ApproveGoogleGenerationSpendAsync(1, true, TestContext.Current.CancellationToken));
        Assert.Equal(0, approvals);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    private static EfNarrationCopyStore CopyStore(AutosaveDiskFixture fixture) => new(
        new AutosaveContextFactory(new DbContextOptionsBuilder<CloudScribeDbContext>().UseSqlite("Data Source=" + fixture.Paths.DatabasePath).Options),
        new DocumentContentStore(fixture.Paths), TimeProvider.System);

    private sealed class BlockingCopies(INarrationCopyStore? inner) : INarrationCopyStore
    {
        public int Prepares { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NarrationCopyPreparation> PrepareAsync(Guid sourceDocumentId, Guid sourceRevisionId, string sourceTextSha256, CancellationToken cancellationToken = default)
        {
            Prepares++; Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner!.PrepareAsync(sourceDocumentId, sourceRevisionId, sourceTextSha256, cancellationToken).ConfigureAwait(false);
        }
        public Task<NarrationCopyPreparation?> FindAsync(Guid narrationDocumentId, CancellationToken cancellationToken = default) =>
            inner?.FindAsync(narrationDocumentId, cancellationToken) ?? Task.FromResult<NarrationCopyPreparation?>(null);
        public Task<DocumentSnapshot> ImportBatchSourceAsync(Guid batchId, Guid itemId, string rawSha256, string title, string normalizedText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
