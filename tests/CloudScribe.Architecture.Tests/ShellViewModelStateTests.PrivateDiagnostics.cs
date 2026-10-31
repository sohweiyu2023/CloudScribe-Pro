using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("consent")]
    public async Task ActualPrivateOptInFailureCannotRetainEarlierArmedCapture(string failure)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        await using var autosave = new DocumentAutosaveCoordinator(fixture.NewLibrary(), TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(fixture.NewLibrary(), autosave);
        await model.NewDocumentCommand.ExecuteAsync(null); model.DocumentText = "Synthetic saved capture request";
        Assert.True(await model.PrepareDocumentCloseAsync());
        model.SelectedVoiceLabVoice = VoiceSelection() with { LanguageCodes = ["en-US"] };
        var store = new GooglePrivateErrorDiagnostics(Path.Combine(fixture.Paths.RootDirectory, "private-failure-tests"), TimeProvider.System);
        var selection = model.CaptureGoogleTtsStudioRequestSelection();
        var old = new PrivateErrorCaptureBinding(selection.DocumentId, selection.RevisionId!.Value, model.PrivateDiagnosticRequestVersion,
            selection.AccountStableId, selection.ProjectStableId, selection.VoiceStableId, "synthetic-credential", new string('a', 64));
        Assert.True(store.CompleteOptInReview(store.BeginOptInReview(), old, true));
        Assert.True(store.IsArmed);
        var compiler = new DelayedPrivateCompilation();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = new GooglePrivateDiagnosticOptIn(store, compiler).PrepareAsync(model,
            () => !string.Equals(failure, "consent", StringComparison.Ordinal), cancellation.Token);
        if (string.Equals(failure, "throw", StringComparison.Ordinal))
        {
            compiler.Fail(new IOException("synthetic private path/token echo"));
            await Assert.ThrowsAsync<IOException>(() => pending);
        }
        else if (string.Equals(failure, "cancel", StringComparison.Ordinal))
        {
            cancellation.Cancel(); compiler.Complete();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else Assert.Null(await pending);
        Assert.False(store.IsArmed); Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("uncheck")]
    [InlineData("close")]
    [InlineData("edit")]
    public async Task ActualPrivateOptInPathCannotRearmAfterDelayedCompileInvalidation(string change)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        await using var autosave = new DocumentAutosaveCoordinator(fixture.NewLibrary(), TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(fixture.NewLibrary(), autosave);
        await model.NewDocumentCommand.ExecuteAsync(null); model.DocumentText = "Synthetic private diagnostic input";
        Assert.True(await model.PrepareDocumentCloseAsync());
        model.SelectedVoiceLabVoice = VoiceSelection() with { LanguageCodes = ["en-US"] };
        var store = new GooglePrivateErrorDiagnostics(Path.Combine(fixture.Paths.RootDirectory, "private-synthetic-tests"), TimeProvider.System);
        model.ConfigurePrivateGoogleDiagnostics((_, _) => throw new InvalidOperationException("No generation expected"), store.Disarm);
        var compiler = new DelayedPrivateCompilation(); bool confirmed = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = new GooglePrivateDiagnosticOptIn(store, compiler).PrepareAsync(model, () => confirmed, cancellation.Token);
        if (string.Equals(change, "uncheck", StringComparison.Ordinal)) confirmed = false;
        if (string.Equals(change, "disable", StringComparison.Ordinal)) store.Disarm();
        if (string.Equals(change, "close", StringComparison.Ordinal)) { store.Disarm(); cancellation.Cancel(); }
        if (string.Equals(change, "edit", StringComparison.Ordinal)) model.DocumentText += " changed";
        compiler.Complete();
        if (string.Equals(change, "close", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (string.Equals(change, "edit", StringComparison.Ordinal)) await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        else Assert.Null(await task);
        Assert.False(store.IsArmed); Assert.False(model.PreparedGoogleGenerationSpendApproved); Assert.Equal(1, compiler.Calls);
    }

    private sealed class DelayedPrivateCompilation : IGoogleRecoveryCurrentCompilation
    {
        private readonly TaskCompletionSource<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ShellViewModel.GoogleTtsStudioRequestSelection? _selection;
        public int Calls { get; private set; }
        public void Fail(Exception error) => _completion.SetException(error);
        public Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileReadonlyAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection, CancellationToken cancellationToken)
        { Calls++; _selection = selection; return _completion.Task; }
        public void Complete()
        {
            var selection = _selection!;
            var envelope = new GoogleGenerationSubmissionEnvelope(selection.AccountStableId, "synthetic-credential", "synthetic-capability", "synthetic-pricing", 1,
                selection.VoiceStableId, "MP3", new string('a', 64), 100);
            // Diagnostic-only seam intentionally has no executable/approved snapshot and cannot submit anything.
            _completion.SetResult(new(envelope, null!, "USD", 2, 3));
        }
    }
}
