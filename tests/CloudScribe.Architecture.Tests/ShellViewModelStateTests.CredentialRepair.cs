using System.Text;
using CloudScribe.App.Composition;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task CredentialRepairRequiresSeparateConsentAndPreservesHistoryAndAcceptedAudio()
    {
        await using var f = await RepairFixture.CreateAsync();
        await f.Disk.NewLibrary().CreateAsync("Synthetic retained draft", "Keep exact synthetic draft.", TestContext.Current.CancellationToken);
        string audio = Path.Combine(f.Disk.Paths.RootDirectory, "synthetic-accepted.mp3");
        await File.WriteAllBytesAsync(audio, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        string retained = await f.RetainedAsync();
        var before = await f.CurrentAsync();
        var service = f.NewService();
        using var controller = new GoogleCredentialRepairController(service, RepairFixture.Account) { IntendedEmail = RepairFixture.Email };
        controller.SelectCandidate(f.Json.AsMemory());
        Assert.Equal(0, f.Handler.Requests);
        Assert.Equal(0, f.Vault.Writes);
        await controller.VerifyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, f.Handler.Requests);
        Assert.Equal(0, f.Vault.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConfirmAsync(TestContext.Current.CancellationToken));
        controller.Acknowledged = true;
        await controller.ConfirmAsync(TestContext.Current.CancellationToken);
        Assert.True(controller.Committed);
        Assert.Equal(3, f.Vault.Writes);
        var after = await f.CurrentAsync();
        Assert.Equal(before.Reference.AccountId, after.Reference.AccountId);
        Assert.Equal(before.Reference.EndpointId, after.Reference.EndpointId);
        Assert.Equal(before.Reference.RegionId, after.Reference.RegionId);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.NotEqual(before.Reference.CredentialReference, after.Reference.CredentialReference);
        Assert.Equal(retained, await f.RetainedAsync());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(audio, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(RepairFixture.OldReference, f.Vault.Items.Keys, StringComparer.Ordinal);
        Assert.Equal(1L, await f.SqlAsync("SELECT COUNT(*) FROM google_credential_repair_events WHERE Phase='Committed';"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConfirmAsync(TestContext.Current.CancellationToken));
        int requests = f.Handler.Requests;
        Assert.Contains("committed", await f.NewService().ReadStatusAsync(RepairFixture.Account, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(requests, f.Handler.Requests);
        string metadata = (string)(await f.SqlAsync("SELECT group_concat(ExpectedAccountJson || IdentityJson) FROM google_credential_repairs;"))!
            + (string)(await f.SqlAsync("SELECT group_concat(ApprovalJson) FROM google_credential_repair_events;"))!;
        Assert.DoesNotContain("BEGIN PRIVATE KEY", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-TOKEN", metadata, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task CredentialRepairRestartReusesOwnedStagingAfterEachWriteFailure(int failure, bool lostAcknowledgement)
    {
        await using var f = await RepairFixture.CreateAsync();
        string retained = await f.RetainedAsync();
        var service = f.NewService();
        var first = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        f.Vault.FailAttempt = failure; f.Vault.FailAfterWrite = lostAcknowledgement;
        await Assert.ThrowsAsync<IOException>(() => service.ConfirmAsync(first.ReviewId, first.ReviewSha256, TestContext.Current.CancellationToken));
        Assert.Equal(RepairFixture.OldReference, (await f.CurrentAsync()).Reference.CredentialReference!.TargetName);
        Assert.Equal(retained, await f.RetainedAsync());
        f.Vault.FailAttempt = 0;
        var restarted = f.NewService();
        var resumed = await restarted.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        Assert.Equal(first.RepairId, resumed.RepairId);
        Assert.Equal(first.NewCredentialReference, resumed.NewCredentialReference);
        Assert.True(resumed.Resuming);
        await restarted.ConfirmAsync(resumed.ReviewId, resumed.ReviewSha256, TestContext.Current.CancellationToken);
        Assert.Equal(3, f.Vault.Writes);
        Assert.Equal(retained, await f.RetainedAsync());
        Assert.Equal(1L, await f.SqlAsync("SELECT COUNT(*) FROM google_credential_repair_events WHERE Phase='Committed';"));
    }

    [Fact]
    public async Task CredentialRepairAccountCasAndCommittedAuditRollBackTogether()
    {
        await using var f = await RepairFixture.CreateAsync();
        string retained = await f.RetainedAsync();
        await f.SqlAsync("""
            CREATE TRIGGER synthetic_commit_failure BEFORE INSERT ON google_credential_repair_events
            WHEN NEW.Phase='Committed' BEGIN SELECT RAISE(ABORT,'Synthetic journal interruption.'); END;
            """);
        var service = f.NewService();
        var review = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => service.ConfirmAsync(review.ReviewId, review.ReviewSha256, TestContext.Current.CancellationToken));
        Assert.Equal(1, (await f.CurrentAsync()).Revision);
        Assert.Equal("Staged", await f.SqlAsync("SELECT Phase FROM google_credential_repairs;"));
        Assert.Equal(retained, await f.RetainedAsync());
        await f.SqlAsync("DROP TRIGGER synthetic_commit_failure;");
        var resumed = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        await service.ConfirmAsync(resumed.ReviewId, resumed.ReviewSha256, TestContext.Current.CancellationToken);
        Assert.Equal(3, f.Vault.Writes);
        await Assert.ThrowsAsync<SqliteException>(() => f.SqlAsync("DELETE FROM google_credential_repair_events;"));
        await Assert.ThrowsAsync<SqliteException>(() => f.SqlAsync("DELETE FROM google_credential_repairs;"));
    }

    [Theory]
    [InlineData(CredentialRolePresence.Unknown)]
    [InlineData(CredentialRolePresence.Present)]
    public async Task CredentialRepairRejectsPresentOrUnknownOldRolesBeforeAuthentication(CredentialRolePresence presence)
    {
        await using var f = await RepairFixture.CreateAsync();
        f.Vault.ForcedPresence = presence;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.NewService().PrepareAsync(
            RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken));
        Assert.Equal(0, f.Handler.Requests);
        Assert.Equal(0, f.Vault.Writes);
    }

    [Fact]
    public async Task CredentialRepairFailedGoogleProjectProofCannotReachConsentOrPersistence()
    {
        await using var f = await RepairFixture.CreateAsync();
        f.Handler.RejectProbe = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.NewService().PrepareAsync(
            RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken));
        Assert.Equal(0, f.Vault.Writes);
        Assert.Equal(0L, await f.SqlAsync("SELECT COUNT(*) FROM google_credential_repairs;"));
    }

    [Fact]
    public async Task CredentialRepairMismatchExpiredRevokedOrChangedAccountCannotWrite()
    {
        await using var f = await RepairFixture.CreateAsync();
        var service = f.NewService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(
            RepairFixture.Account, "wrong@synthetic-project.iam.gserviceaccount.com", f.Json.AsMemory(), TestContext.Current.CancellationToken));
        var revoked = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        service.Discard(revoked.ReviewId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(revoked.ReviewId, revoked.ReviewSha256, TestContext.Current.CancellationToken));
        var expired = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        f.Clock.Now = expired.ExpiresAtUtc;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(expired.ReviewId, expired.ReviewSha256, TestContext.Current.CancellationToken));
        var changed = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        await f.SqlAsync("UPDATE provider_accounts SET Revision=Revision+1;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(changed.ReviewId, changed.ReviewSha256, TestContext.Current.CancellationToken));
        Assert.Equal(0, f.Vault.Writes);
    }
}
