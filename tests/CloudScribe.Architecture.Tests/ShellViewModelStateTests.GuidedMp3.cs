using CloudScribe.App.ViewModels;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task GuidedCreateRequiresExplicitConsentBeforeApprovalOrRuntime()
    {
        using ShellViewModel model = CreateViewModel();
        ConfigureGoogleSpendReview(model);
        int approvals = 0;
        model.ConfigureStage6GoogleGenerationSpendApproval((_, _, _) => { approvals++; return Task.CompletedTask; });
        var review = await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.CreateReviewedGoogleMp3Async(review, false, TestContext.Current.CancellationToken));
        Assert.Equal(0, approvals);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    [Fact]
    public async Task GuidedCreateRejectsOldReviewEvenWhenCostAndPayloadMatch()
    {
        using ShellViewModel model = CreateViewModel();
        ConfigureGoogleSpendReview(model);
        var old = await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.CreateReviewedGoogleMp3Async(old, true, TestContext.Current.CancellationToken));
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    [Fact]
    public async Task GuidedCreateUsesExactCeilingAndExistingSingleUseRuntime()
    {
        using ShellViewModel model = CreateViewModel();
        ConfigureGoogleSpendReview(model);
        long ceiling = -1;
        int attempts = 0;
        model.ConfigureStage6GoogleGenerationSpendApproval((maximum, confirmed, _) =>
        {
            Assert.True(confirmed); ceiling = maximum; return Task.CompletedTask;
        });
        model.ConfigureStage6GoogleGeneration(_ =>
        {
            attempts++;
            throw new InvalidOperationException("Isolated mock stops before provider dispatch.");
        });
        var review = await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await model.CreateReviewedGoogleMp3Async(review, true, TestContext.Current.CancellationToken);
        Assert.Equal(review.CurrentEstimateMinorUnits, ceiling);
        Assert.Equal(1, attempts);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.CreateReviewedGoogleMp3Async(review, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task GuidedCreateCannotDispatchAfterEditDuringApproval()
    {
        using ShellViewModel model = CreateViewModel();
        ConfigureGoogleSpendReview(model);
        var approving = new TaskCompletionSource();
        int attempts = 0;
        model.ConfigureStage6GoogleGenerationSpendApproval((_, _, _) => approving.Task);
        model.ConfigureStage6GoogleGeneration(_ => { attempts++; throw new InvalidOperationException("Unexpected dispatch."); });
        var review = await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        Task create = model.CreateReviewedGoogleMp3Async(review, true, TestContext.Current.CancellationToken);
        model.DocumentText = "Changed while approval is being persisted";
        approving.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => create);
        Assert.Equal(0, attempts);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }
}
