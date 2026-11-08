#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Text.Json;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Fact]
    public async Task LongAudioRenewalReplacesOnlyUncommittedApprovals()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        string install = Path.Combine(w.Root, "install"), authorityPath = Path.Combine(install, "batch-authority");
        Directory.CreateDirectory(authorityPath);
        var profile = Profile(w.Root, BatchAuthorityMode.InAppWithinLimits);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, InstalledBatchAuthorityStore.Json);
        await File.WriteAllBytesAsync(Path.Combine(authorityPath, "profile.json"), bytes, Token);
        await File.WriteAllTextAsync(Path.Combine(authorityPath, "pins.json"), JsonSerializer.Serialize(new BatchAuthorityPins(1,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), new Dictionary<string, string>(StringComparer.Ordinal))), Token);
        var store = new InstalledBatchAuthorityStore(Path.Combine(install, "app"), w.Root);
        var scope = BatchScope();
        string pin = new('d', 64), ledger = Path.Combine(w.Root, "ledger.json");
        var source = new BatchLongAudioScopeExtensionSource(store, new FixedClock(Now));
        var old = BatchApproval('a');
        var replacement = BatchApproval('b');
        var first = Approve(batch, old, Now.AddMinutes(-2));
        var second = Approve(batch, replacement, Now.AddMinutes(-1));
        await store.SaveInAppConsentAsync(first, Token);
        await store.SaveInAppConsentAsync(second, Token);
        await SaveLedger([]);
        var jobs = await source.LoadAsync(scope, pin, ledger, 0, Token);
        Assert.Equal(replacement.AttemptId, Assert.Single(jobs.Jobs).AttemptId);

        // A committed old request keeps exactly its original authority, even after renewal and expiry.
        await SaveLedger([new(old.AttemptId, old.RequestBindingSha256, old.CeilingUsdCents, null)]);
        jobs = await new BatchLongAudioScopeExtensionSource(store, new FixedClock(Now.AddDays(3))).LoadAsync(scope, pin, ledger, 0, Token);
        var held = Assert.Single(jobs.Jobs);
        Assert.Equal(old.AttemptId, held.AttemptId);
        Assert.Equal(old.FamilyMaximumUsdCents, held.FamilyMaximumUsdCents);
        Assert.True(held.Committed);

        // A later renewal cannot displace an earlier approval belonging to another batch.
        await SaveLedger([]);
        var other = await w.CreateAsync(["other.txt"]);
        var otherApproval = BatchApproval('c');
        await store.SaveInAppConsentAsync(Approve(other, otherApproval, Now.AddMinutes(-1.5)), Token);
        jobs = await source.LoadAsync(scope with { TotalGoogleCapUsdCents = otherApproval.FamilyMaximumUsdCents }, pin, ledger, 0, Token);
        Assert.Equal(otherApproval.AttemptId, Assert.Single(jobs.Jobs).AttemptId);

        BatchConsent Approve(NarrationBatch queue, BatchLongAudioApproval la, DateTimeOffset at)
        {
            var item = queue.Items[0];
            return BatchConsent.Create(Guid.NewGuid(), queue, BatchConsentSource.InApp, profile, Account, Project,
                [new(item.ItemId, item.SourceSha256, item.DocumentId!.Value, item.RevisionId!.Value, la.RequestBindingSha256,
                    la.FamilyMaximumUsdCents, BatchEngines.LongAudio, la)], at);
        }
        Task SaveLedger(IReadOnlyList<GoogleLongAudioBudgetClaim> claims) =>
            File.WriteAllTextAsync(ledger, JsonSerializer.Serialize(new GoogleLongAudioBudgetLedger(1, pin, claims)), Token);
    }

    [Fact]
    public void LongAudioRetainedHeadroomCreditsOnlyTheSameCommittedJob()
    {
        var scope = BatchScope();
        var approval = BatchApproval('a');
        var context = new BatchLongAudioPlanner.Context(new("p", new string('c', 64), "s", new string('d', 64), "l", "o", "u", scope),
            0, approval.FamilyMaximumUsdCents, null, new Dictionary<Guid, long> { [approval.AttemptId] = approval.FamilyMaximumUsdCents });
        Assert.Equal(approval.FamilyMaximumUsdCents, context.RetainedCredit(approval, approval.AttemptId));
        Assert.Equal(0, context.RetainedCredit(approval, null));
        Assert.Equal(0, context.RetainedCredit(approval, Guid.NewGuid()));
        Assert.Equal(0, context.RetainedCredit(BatchApproval('b'), approval.AttemptId));
        Assert.Equal(approval.FamilyMaximumUsdCents, context.RetainedCredit(approval with { FamilyMaximumUsdCents = 100 }, approval.AttemptId));
    }

    [Fact]
    public void LongAudioFamilyFundsBothIndependentlyRoundedChildren()
    {
        var scope = BatchScope();
        var context = new BatchLongAudioPlanner.Context(new("p", new string('c', 64), "s", new string('d', 64), "l", "o", "u", scope), 100, 100, null);
        var batch = new NarrationBatch(2, Guid.NewGuid(), "b", 1, BatchQueueState.Ready, Settings, Settings.Sha256(), "out", "USD", 2, [], Now);
        var item = new BatchItem(Guid.NewGuid(), 1, "a.txt", Sha("A. B."), 5, 5, "a.mp3", BatchItemState.Ready);
        var approval = Assert.IsType<BatchLongAudioApproval>(BatchLongAudioPlanner.Plan(context, batch, item, "A. B.").Approval);
        Assert.Equal(1, approval.CeilingUsdCents);
        Assert.Equal(3, approval.FamilyMaximumUsdCents); // root plus two minimum-one-cent child commitments
    }

    [Fact]
    public async Task RenewedConsentStillCountsFinishedFilesAgainstBatchAndRollingCaps()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["done.txt", "remaining.txt"]);
        batch = batch with { Items = [batch.Items[0] with { State = BatchItemState.Completed, JobId = Guid.NewGuid(), ReservedMinorUnits = 200 }, batch.Items[1]] };
        var profile = Profile(w.Root) with { PerBatchCeilingMinorUnits = 300, RollingCeilingMinorUnits = 500 };
        BatchConsent Renew(BatchAuthorityProfile p) => BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.ReviewedInstall,
            p, Account, Project, [new(batch.Items[1].ItemId, batch.Items[1].SourceSha256, batch.Items[1].DocumentId!.Value,
                batch.Items[1].RevisionId!.Value, "plan", 200)], Now);
        Assert.Equal("batch.ceiling-batch", Code(profile, Renew(profile), batch));
        profile = profile with { PerBatchCeilingMinorUnits = 500 };
        Assert.Equal("batch.ceiling-rolling", Code(profile, Renew(profile), batch, others: 101));
        Assert.Equal("batch.authorized", Code(profile, Renew(profile), batch, others: 100));
        // An original consent already covering the completed file is not counted twice.
        Assert.Equal("batch.authorized", Code(profile, Consent(batch, profile, perFile: 200), batch, others: 100));
    }

    private static GoogleLongAudioBudgetScope BatchScope() => new(1, new string('a', 64), new string('b', 64), "123456", "cred",
        "cmn-CN-Chirp3-HD-Alnilam", "cmn-CN", "LINEAR16", "private-bucket", 100, 0, 30m, "fixture",
        new Dictionary<string, long>(StringComparer.Ordinal));

    private static BatchLongAudioApproval BatchApproval(char hash)
    {
        var attempt = Guid.NewGuid();
        return new(attempt, $"gs://private-bucket/batch/{attempt:N}.wav", new string(hash, 64), 1, 3, 1000, 24000, 1, true);
    }
}
