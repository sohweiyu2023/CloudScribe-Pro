using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task SameCountDifferentQueueIdentitiesCannotEscapeBudgetAccounting()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var (admission, budget) = await CreateInstalledBudgetAsync(f, e);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        var review = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        await service.ApproveAsync(Confirmation(review), TestContext.Current.CancellationToken);
        string install = Path.Combine(f.Root, "synthetic-install");
        var policy = JsonSerializer.Deserialize<RecoveryAdmissionPolicy>(File.ReadAllBytes(Path.Combine(install, "recovery-admission-policy.json")))!;
        // Deliberately inconsistent independently admitted synthetic baseline: child is pinned as old,
        // but its reservation is still derived as new. A foreign extra queue must not substitute by count.
        policy = policy with { Budget = policy.Budget with { QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3) } };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy);
        File.WriteAllBytes(Path.Combine(install, "recovery-admission-policy.json"), bytes);
        File.WriteAllText(Path.Combine(install, "cloudscribe-install.json"), JsonSerializer.Serialize(new
        {
            Product = "CloudScribe Pro",
            AppSha256 = policy.AppSha256,
            RecoveryPolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        }));
        await new GoogleGenerationPersistedQueueStateStore(f).SaveAsync(new(e.Previous.AccountId, e.Previous.OperationStableId,
            "unaccounted-foreign-queue", false, null), TestContext.Current.CancellationToken);
        Assert.Null(await budget.ResolveAsync(e.Intent.Envelope.AccountId, e.Intent.ProjectId, "USD", 2, await FinancialHashAsync(f), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcreteInstalledBudgetRetainsReservationsAcrossApproveClaimRestartAndDescendant()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var (admission, budget) = await CreateInstalledBudgetAsync(f, e);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        var review = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        Assert.Equal(250, review.Budget.RetainedCommittedMinorUnits); Assert.Equal(70, review.Budget.RemainingAllocationMinorUnits);
        var approved = await service.ApproveAsync(Confirmation(review), TestContext.Current.CancellationToken);
        var envelope = e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId };
        var store = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, service);
        var spend = await store.LoadApprovedAsync(envelope, TestContext.Current.CancellationToken);
        Assert.NotNull(spend); Assert.Equal(3, spend.AuthorizedMaximumMinorUnits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveApprovedAsync(spend, TestContext.Current.CancellationToken));
        Assert.Equal(e.OldSpend, await store.LoadApprovedAsync(e.Intent.Envelope, TestContext.Current.CancellationToken));
        await AssertConcreteBudgetAsync(f, e, budget, 253, 67);
        var claim = ClaimRecovery(e, approved);
        Assert.True(await service.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3, claim, TestContext.Current.CancellationToken));
        await AssertConcreteBudgetAsync(f, e, budget, 253, 67);
        var restarted = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        Assert.Null(await restarted.LoadExecutionAuthorizationAsync(envelope, TestContext.Current.CancellationToken));
        Assert.False(await restarted.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3, claim, TestContext.Current.CancellationToken));
        var next = await restarted.PrepareAsync(e.Intent with { PreviousIdempotencyKey = approved.IdempotencyKey }, 3, TestContext.Current.CancellationToken);
        var second = await restarted.ApproveAsync(Confirmation(next), TestContext.Current.CancellationToken);
        Assert.NotEqual(approved.AuthorizationId, second.AuthorizationId);
        await AssertConcreteBudgetAsync(f, e, budget, 256, 64);
        Assert.Equal(e.Previous, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Previous.AccountId,
            e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("unknown-prefix")]
    [InlineData("foreign-spend")]
    [InlineData("mutated-hold")]
    [InlineData("stale-digest")]
    [InlineData("wrong-scope")]
    public async Task ConcreteBudgetRejectsUnaccountedFinancialChanges(string change)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var (_, budget) = await CreateInstalledBudgetAsync(f, e);
        if (string.Equals(change, "unknown-prefix", StringComparison.Ordinal))
            await new GoogleGenerationPersistedQueueStateStore(f).SaveAsync(new(e.Previous.AccountId, e.Previous.OperationStableId,
                GoogleExplicitRecoveryService.KeyPrefix + Guid.NewGuid().ToString("N"), false, null), TestContext.Current.CancellationToken);
        if (string.Equals(change, "foreign-spend", StringComparison.Ordinal))
            await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).SaveApprovedAsync(
                GoogleGenerationSpendAuthorization.Create(e.Intent.Envelope with { RequestRevision = 43 }, "USD", 2, 1, 1), TestContext.Current.CancellationToken);
        if (string.Equals(change, "mutated-hold", StringComparison.Ordinal))
        {
            using var context = f.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync("UPDATE google_generation_queue_states SET DiagnosticCode='changed';", TestContext.Current.CancellationToken);
        }
        string digest = string.Equals(change, "stale-digest", StringComparison.Ordinal) ? new string('a', 64) : await FinancialHashAsync(f);
        Assert.Null(await budget.ResolveAsync(e.Intent.Envelope.AccountId,
            string.Equals(change, "wrong-scope", StringComparison.Ordinal) ? "another-project" : e.Intent.ProjectId,
            "USD", 2, digest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DurableRecoveryIdCannotFallBackToGenericApprovalOrDifferentChild()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var (admission, budget) = await CreateInstalledBudgetAsync(f, e);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approval = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        var store = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, service);
        Assert.Null(await store.LoadApprovedAsync(e.Intent.Envelope with { RecoveryAuthorizationId = Guid.NewGuid() }, TestContext.Current.CancellationToken));
        var envelope = e.Intent.Envelope with { RecoveryAuthorizationId = approval.AuthorizationId };
        Assert.Null(await store.LoadApprovedAsync(envelope with { PricingProvenanceId = "changed" }, TestContext.Current.CancellationToken));
        Assert.False(await service.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3,
            ClaimRecovery(e, approval) with { IdempotencyKey = e.Previous.IdempotencyKey }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryClaimAsync(approval, ClaimRecovery(e, approval),
            e.Intent.Envelope, e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        await AssertConcreteBudgetAsync(f, e, budget, 253, 67);
    }

    private static async Task AssertConcreteBudgetAsync(Fixture f, RecoveryEvidence e, InstalledRecoveryBudgetEvidenceResolver resolver,
        long committed, long remaining)
    {
        var current = await resolver.ResolveAsync(e.Intent.Envelope.AccountId, e.Intent.ProjectId, "USD", 2, await FinancialHashAsync(f).ConfigureAwait(false), TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.NotNull(current); Assert.Equal(committed, current.ApprovedBudget.RetainedCommittedMinorUnits);
        Assert.Equal(remaining, current.ApprovedBudget.RemainingAllocationMinorUnits);
    }

    private static async Task<(InstalledRecoveryAdmissionStore Admission, InstalledRecoveryBudgetEvidenceResolver Budget)> CreateInstalledBudgetAsync(Fixture f, RecoveryEvidence e)
    {
        string install = Path.Combine(f.Root, "synthetic-install"); string app = Path.Combine(install, "app"); Directory.CreateDirectory(app);
        byte[] module = "synthetic-non-executable-module"u8.ToArray();
        File.WriteAllBytes(Path.Combine(app, "CloudScribe.dll"), module); File.WriteAllBytes(Path.Combine(app, "CloudScribe.Infrastructure.dll"), module);
        string moduleHash = Convert.ToHexStringLower(SHA256.HashData(module));
        var baseline = new RecoveryBudgetBaseline(250, 1000, 10, 80, new string('a', 64),
            await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false), await RowPinsAsync(f, "google_generation_spend_authorizations", 1).ConfigureAwait(false),
            await RowPinsAsync(f, "google_recovery_attempts", 1).ConfigureAwait(false));
        var policy = new RecoveryAdmissionPolicy(1, moduleHash, moduleHash, InstalledRecoveryAdmissionStore.ProfileHash(f.Paths.RootDirectory),
            e.Intent.Envelope.AccountId, e.Intent.ProjectId, "USD", 2, DateTimeOffset.UtcNow.AddHours(1), baseline, [], [e.Association.Value!]);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy);
        File.WriteAllBytes(Path.Combine(install, "recovery-admission-policy.json"), bytes);
        File.WriteAllText(Path.Combine(install, "cloudscribe-install.json"), JsonSerializer.Serialize(new
        {
            Product = "CloudScribe Pro",
            AppSha256 = moduleHash,
            RecoveryPolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        }));
        var admission = new InstalledRecoveryAdmissionStore(app + Path.DirectorySeparatorChar, f.Paths, TimeProvider.System);
        return (admission, new(admission, f));
    }

    private static async Task<RecoveryFinancialRowPin[]> RowPinsAsync(Fixture f, string table, int identityColumns) =>
        (await FinancialRowsAsync(f, table).ConfigureAwait(false)).Select(row => new RecoveryFinancialRowPin(JsonHash(row.Take(identityColumns).ToArray()), JsonHash(row))).ToArray();
    private static async Task<string> FinancialHashAsync(Fixture f) => JsonHash((await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false))
        .Concat(await FinancialRowsAsync(f, "google_generation_spend_authorizations").ConfigureAwait(false)).ToArray());
    private static string JsonHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static async Task<List<object?[]>> FinancialRowsAsync(Fixture f, string table)
    {
        using var context = f.CreateDbContext(); var connection = context.Database.GetDbConnection(); await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
        using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false); List<object?[]> rows = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
        { object[] values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values.Select(x => x is DBNull ? null : x).ToArray()); }
        return rows;
    }
}
