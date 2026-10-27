using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task RecoveryReviewAndApprovalDoNotInvokeGeneration()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        await using var autosave = new DocumentAutosaveCoordinator(fixture.NewLibrary(), TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(fixture.NewLibrary(), autosave);
        await model.NewDocumentCommand.ExecuteAsync(null); model.DocumentText = "Synthetic recovery text";
        model.SelectedVoiceLabVoice = VoiceSelection() with { LanguageCodes = ["en-US"] };
        int approvals = 0, executions = 0;
        model.ConfigureGoogleRecovery((selection, _, _, _) => Task.FromResult(RecoveryPreviewForUi(selection)),
            (_, _, _) => { approvals++; return Task.CompletedTask; });
        model.ConfigureStage6GoogleGeneration(_ => { executions++; throw new InvalidOperationException("No generation expected."); });
        var review = await model.PrepareGoogleRecoveryAsync("synthetic-held-key", 3, TestContext.Current.CancellationToken);
        Assert.False(model.PreparedGoogleGenerationSpendApproved); Assert.Equal(0, approvals); Assert.Equal(0, executions);
        await model.ApproveGoogleRecoveryAsync(new(review.PreviewId, review.ReviewSha256, 3, true, true), TestContext.Current.CancellationToken);
        Assert.True(model.PreparedGoogleGenerationSpendApproved); Assert.Equal(1, approvals); Assert.Equal(0, executions);
        model.DocumentText += " changed";
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    [Theory]
    [InlineData("changed-text")]
    [InlineData("changed-maximum")]
    [InlineData("changed-review")]
    public async Task RecoveryUiRejectsChangedSelectionOrDisplayedReviewBeforeApproval(string change)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        await using var autosave = new DocumentAutosaveCoordinator(fixture.NewLibrary(), TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(fixture.NewLibrary(), autosave);
        await model.NewDocumentCommand.ExecuteAsync(null); model.DocumentText = "Synthetic recovery text";
        model.SelectedVoiceLabVoice = VoiceSelection() with { LanguageCodes = ["en-US"] };
        int approvals = 0;
        model.ConfigureGoogleRecovery((selection, _, _, _) => Task.FromResult(RecoveryPreviewForUi(selection)),
            (_, _, _) => { approvals++; return Task.CompletedTask; });
        var review = await model.PrepareGoogleRecoveryAsync("synthetic-held-key", 3, TestContext.Current.CancellationToken);
        if (string.Equals(change, "changed-text", StringComparison.Ordinal)) model.DocumentText += " changed";
        var confirmation = new GoogleRecoveryApprovalConfirmation(review.PreviewId,
            string.Equals(change, "changed-review", StringComparison.Ordinal) ? new string('b', 64) : review.ReviewSha256,
            string.Equals(change, "changed-maximum", StringComparison.Ordinal) ? 4 : 3, true, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ApproveGoogleRecoveryAsync(confirmation, TestContext.Current.CancellationToken));
        Assert.Equal(0, approvals); Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    [Fact]
    public async Task SelectionChangedDuringRecoveryPreparationCannotPublishStaleReview()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        await using var autosave = new DocumentAutosaveCoordinator(fixture.NewLibrary(), TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(fixture.NewLibrary(), autosave);
        await model.NewDocumentCommand.ExecuteAsync(null); model.DocumentText = "Synthetic recovery text";
        model.SelectedVoiceLabVoice = VoiceSelection() with { LanguageCodes = ["en-US"] };
        var completion = new TaskCompletionSource<GoogleRecoveryPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selection = model.CaptureGoogleTtsStudioRequestSelection();
        model.ConfigureGoogleRecovery((_, _, _, _) => completion.Task, (_, _, _) => throw new InvalidOperationException("No approval expected."));
        var prepare = model.PrepareGoogleRecoveryAsync("synthetic-held-key", 3, TestContext.Current.CancellationToken);
        model.DocumentText += " changed";
        completion.SetResult(RecoveryPreviewForUi(selection));
        await Assert.ThrowsAsync<InvalidOperationException>(() => prepare);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    private static GoogleRecoveryPreview RecoveryPreviewForUi(ShellViewModel.GoogleTtsStudioRequestSelection selection)
    {
        var envelope = new GoogleGenerationSubmissionEnvelope(selection.AccountStableId, "synthetic-credential", "synthetic-capability",
            "synthetic-pricing", 1, selection.VoiceStableId, "MP3", new string('a', 64), 100);
        var intent = new GoogleRecoveryIntent(selection.DocumentId, selection.RevisionId ?? Guid.NewGuid(), selection.ProjectStableId,
            "synthetic-held-key", envelope, "USD", 2, 3, new byte[100]);
        return new(Guid.NewGuid(), new string('a', 64), "synthetic-logical", intent,
            new(selection.AccountStableId, GoogleGenerationProvider.SynthesizeOperationStableId, intent.PreviousIdempotencyKey, false, null),
            new(250, 1000, 70, new string('a', 64)), 3, new string('b', 64), new("synthetic-logical", intent.PreviousIdempotencyKey, new string('c', 64), new string('d', 64)));
    }
}
