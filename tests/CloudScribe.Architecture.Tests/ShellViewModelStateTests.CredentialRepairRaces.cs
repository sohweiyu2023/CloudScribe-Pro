using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CredentialRepairFileReplacementOrCancelledPickerInvalidatesPreviousConsent(bool beginPicker)
    {
        await using var f = await RepairFixture.CreateAsync();
        var service = f.NewService();
        using var controller = new GoogleCredentialRepairController(service, RepairFixture.Account) { IntendedEmail = RepairFixture.Email };
        controller.SelectCandidate(f.Json.AsMemory());
        await controller.VerifyAsync(TestContext.Current.CancellationToken);
        controller.Acknowledged = true;
        if (beginPicker) controller.BeginCandidateSelection();
        Assert.Throws<InvalidDataException>(() => controller.SelectCandidate(ReadOnlyMemory<char>.Empty));
        Assert.Null(controller.Review);
        Assert.False(controller.Acknowledged);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConfirmAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, f.Vault.Writes);
    }

    [Fact]
    public async Task CredentialRepairCandidateChangeDuringDelayedGoogleVerificationCannotPublishReview()
    {
        await using var f = await RepairFixture.CreateAsync();
        f.Handler.WaitBeforeResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = new GoogleCredentialRepairController(f.NewService(), RepairFixture.Account) { IntendedEmail = RepairFixture.Email };
        controller.SelectCandidate(f.Json.AsMemory());
        var verification = controller.VerifyAsync(TestContext.Current.CancellationToken);
        await f.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        controller.BeginCandidateSelection();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
        Assert.Null(controller.Review);
        Assert.False(controller.Acknowledged);
        Assert.Equal(0, f.Vault.Writes);
    }

    [Fact]
    public async Task CredentialRepairRejectsAlteredStagingWithoutOverwriteOrAccountChange()
    {
        await using var f = await RepairFixture.CreateAsync();
        var service = f.NewService();
        var review = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        f.Vault.FailAttempt = 1; f.Vault.FailAfterWrite = true;
        await Assert.ThrowsAsync<IOException>(() => service.ConfirmAsync(review.ReviewId, review.ReviewSha256, TestContext.Current.CancellationToken));
        string staged = Assert.Single(f.Vault.Items.Keys);
        Array.Clear(f.Vault.Items[staged]);
        f.Vault.Items[staged] = "SYNTHETIC-ALTERED".ToCharArray();
        f.Vault.FailAttempt = 0;
        var resumed = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(resumed.ReviewId, resumed.ReviewSha256, TestContext.Current.CancellationToken));
        Assert.Equal(1, f.Vault.Writes);
        Assert.Equal(1, (await f.CurrentAsync()).Revision);
    }

    [Fact]
    public async Task CredentialRepairConcurrentConfirmationsCannotCreateTwoBindings()
    {
        await using var f = await RepairFixture.CreateAsync();
        var first = f.NewService(); var second = f.NewService();
        var a = await first.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        var b = await second.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Vault.BeforeStore = async attempt => { if (attempt == 1) { entered.TrySetResult(); await release.Task.ConfigureAwait(false); } };
        var writing = first.ConfirmAsync(a.ReviewId, a.ReviewSha256, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => second.ConfirmAsync(b.ReviewId, b.ReviewSha256, TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); }
        await writing;
        Assert.Equal(3, f.Vault.Writes);
        Assert.Equal(1L, await f.SqlAsync("SELECT COUNT(*) FROM google_credential_repairs;"));
    }

    [Fact]
    public async Task CredentialRepairActiveJournalBlocksPaidQueueAndKeepsFinancialRows()
    {
        await using var f = await RepairFixture.CreateAsync();
        string retained = await f.RetainedAsync();
        var service = f.NewService();
        var review = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        f.Vault.FailAttempt = 1;
        await Assert.ThrowsAsync<IOException>(() => service.ConfirmAsync(review.ReviewId, review.ReviewSha256, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => f.SqlAsync("""
            INSERT INTO google_generation_queue_states(AccountId,OperationStableId,IdempotencyKey,UnresolvedSubmission,SubmissionAttempted)
                VALUES('synthetic-google','synthesize-speech','must-not-submit',0,0);
            """));
        await Assert.ThrowsAsync<SqliteException>(() => f.SqlAsync("DELETE FROM google_generation_spend_authorizations;"));
        Assert.Equal(retained, await f.RetainedAsync());
    }

    [Fact]
    public async Task CredentialRepairCannotStartWhileVoiceLabAuditionIsAlreadyInFlight()
    {
        using var model = CreateViewModel();
        var selected = VoiceSelection();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.SelectedVoiceLabVoice = selected;
        model.ConfigureStage7VoiceLabAudition(
            async (_, _, _) => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); throw new InvalidOperationException("Synthetic stop before provider."); },
            (_, _) => Task.FromResult(new VoiceLabAuditionRequest(selected, false, true, false, true, "mp3")),
            (selection, _) => Task.FromResult(selection));
        var audition = model.AuditionSelectedVoiceCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Throws<InvalidOperationException>(() => model.BeginGoogleCredentialRepair());
        }
        finally { release.TrySetResult(); }
        await audition;
        model.BeginGoogleCredentialRepair();
        Assert.False(model.CanAuditionSelectedVoice);
        Assert.False(model.CanPrepareGoogleGenerationSpend);
        Assert.False(model.CanApproveGoogleGenerationSpend);
        model.EndGoogleCredentialRepairWithoutMutation();
    }
}
