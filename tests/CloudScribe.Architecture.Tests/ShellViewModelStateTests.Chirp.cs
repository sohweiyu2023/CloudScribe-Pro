using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task ChirpUiReservesOnlyAfterSavedReviewAndNeverInvokesGenerationOrPrivateCapture()
    {
        await using var f = await AutosaveDiskFixture.CreateAsync();
        await using var autosave = new DocumentAutosaveCoordinator(f.NewLibrary(), TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(f.NewLibrary(), autosave);
        await model.NewDocumentCommand.ExecuteAsync(null); model.DocumentText = "Synthetic original article";
        model.SelectedVoiceLabVoice = VoiceSelection() with { LanguageCodes = ["en-US"] };
        int approvals = 0, executions = 0, attachments = 0;
        model.ConfigurePrivateGoogleDiagnostics((_, _) => attachments++, () => { });
        model.ConfigureChirpArticle((s, _, _) => Task.FromResult(ChirpUiReview(s)),
            (_, _, _) => { approvals++; return Task.CompletedTask; }, (_, _, _) => throw new InvalidOperationException("No accepted output expected."));
        model.ConfigureStage6GoogleGeneration(_ => { executions++; throw new InvalidOperationException("No provider request expected."); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.PrepareChirpArticleAsync(1, TestContext.Current.CancellationToken));
        Assert.True(await model.PrepareDocumentCloseAsync());
        var review = await model.PrepareChirpArticleAsync(1, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ApproveChirpArticleAsync(new(review.PreviewId, review.ReviewSha256, 1, true, false), TestContext.Current.CancellationToken));
        Assert.Equal(0, approvals); Assert.False(model.PreparedGoogleGenerationSpendApproved);
        await model.ApproveChirpArticleAsync(new(review.PreviewId, review.ReviewSha256, 1, true, true), TestContext.Current.CancellationToken);
        Assert.True(model.PreparedGoogleGenerationSpendApproved); Assert.Equal(1, approvals); Assert.Equal(0, executions); Assert.Equal(0, attachments);
        model.DocumentText += " changed"; Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChirpNavigationDuringAsyncPreparationOrApprovalCannotLeaveExecutableState(bool duringApproval)
    {
        await using var f = await AutosaveDiskFixture.CreateAsync();
        await using var autosave = new DocumentAutosaveCoordinator(f.NewLibrary(), TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(f.NewLibrary(), autosave);
        await model.NewDocumentCommand.ExecuteAsync(null); model.DocumentText = "Synthetic original article";
        model.SelectedVoiceLabVoice = VoiceSelection() with { LanguageCodes = ["en-US"] };
        Assert.True(await model.PrepareDocumentCloseAsync());
        bool activated = false; var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ConfigureGoogleRequestInvalidation(() => activated = false);
        model.ConfigureChirpArticle(async (s, _, token) =>
            { if (!duringApproval) await block.Task.WaitAsync(token).ConfigureAwait(false); return ChirpUiReview(s); },
            async (_, _, token) => { await block.Task.WaitAsync(token).ConfigureAwait(false); activated = true; },
            (_, _, _) => throw new InvalidOperationException("No output expected."));
        Task operation;
        if (duringApproval)
        {
            var review = await model.PrepareChirpArticleAsync(1, TestContext.Current.CancellationToken);
            operation = model.ApproveChirpArticleAsync(new(review.PreviewId, review.ReviewSha256, 1, true, true), TestContext.Current.CancellationToken);
        }
        else operation = model.PrepareChirpArticleAsync(1, TestContext.Current.CancellationToken);
        model.DocumentText += " changed during wait"; block.SetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.False(activated); Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    private static ChirpRequestReview ChirpUiReview(ShellViewModel.GoogleTtsStudioRequestSelection selection) => new(
        Guid.NewGuid(), new string('a', 64), new string('b', 64), 1, selection.DocumentId, selection.RevisionId!.Value,
        new string('c', 64), 100, "USD", 2, 1, 1, 148, 126, null, DateTimeOffset.UtcNow.AddHours(1), "synthetic-pricing", selection.VoiceStableId);
}
