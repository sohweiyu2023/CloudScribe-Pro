using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task InterruptedApprovalAndClaimTransactionsNeverPublishPartialFinancialState()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var p = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        using (var context = f.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_approval_failure BEFORE INSERT ON google_recovery_attempts BEGIN SELECT RAISE(ABORT,'synthetic-approval-interruption'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken));
        using (var context = f.CreateDbContext())
        {
            Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_generation_queue_states").SingleAsync(TestContext.Current.CancellationToken));
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_approval_failure;", TestContext.Current.CancellationToken);
        }
        p = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken);
        using (var context = f.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_claim_failure BEFORE UPDATE ON google_generation_queue_states WHEN NEW.IdempotencyKey LIKE 'recovery-google-tts:%' BEGIN SELECT RAISE(ABORT,'synthetic-claim-interruption'); END;", TestContext.Current.CancellationToken);
        var claim = ClaimRecovery(e, approved);
        await Assert.ThrowsAsync<SqliteException>(() => service.TryClaimAsync(approved, claim, e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId }, e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        using (var context = f.CreateDbContext())
        {
            Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT Consumed AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_claim_failure;", TestContext.Current.CancellationToken);
        }
        Assert.True(await service.TryClaimAsync(approved, claim, e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId }, e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        Assert.Equal(e.Previous, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task RecoveryMigrationPreservesPriorAttemptAndSpendWithoutCreatingApprovals()
    {
        using var f = new Fixture();
        using (var previous = f.CreateDbContext())
            await previous.GetService<IMigrator>().MigrateAsync(CloudScribe.Infrastructure.Persistence.Migrations.NarrationPartGroups.MigrationId, TestContext.Current.CancellationToken);
        var e = await RecoveryEvidenceAsync(f);
        await f.InitializeAsync();
        var store = new GoogleGenerationPersistedQueueStateStore(f);
        Assert.Equal(e.Previous, await store.LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.Equal(e.OldSpend, await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).LoadApprovedAsync(e.Intent.Envelope, TestContext.Current.CancellationToken));
        using var context = f.CreateDbContext();
        Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task AdditionalAttemptRequiresFreshExactApprovalAndRetainsHistoricalState()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(Confirmation(preview) with { AcknowledgedHistoricalHolds = false }, TestContext.Current.CancellationToken));
        var approval = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        var restarted = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        Assert.Equal(approval, await restarted.FindApprovedAsync(preview.ReviewSha256, TestContext.Current.CancellationToken));
        var store = new GoogleGenerationPersistedQueueStateStore(f);
        Assert.Equal(e.Previous, await store.LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
        var claim = ClaimRecovery(e, approval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken));
        bool[] claims = await Task.WhenAll(service.TryClaimAsync(approval, claim, e.Intent.Envelope with { RecoveryAuthorizationId = approval.AuthorizationId }, e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken),
            restarted.TryClaimAsync(approval, claim with { LocalAttemptId = Guid.NewGuid().ToString("N") }, e.Intent.Envelope with { RecoveryAuthorizationId = approval.AuthorizationId }, e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        Assert.Single(claims, x => x);
        Assert.False(await restarted.TryClaimAsync(approval, claim, e.Intent.Envelope with { RecoveryAuthorizationId = approval.AuthorizationId }, e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        Assert.Equal(e.Previous, await store.LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.Equal(e.OldSpend, await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).LoadApprovedAsync(e.Intent.Envelope, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("envelope")]
    [InlineData("project")]
    [InlineData("document")]
    public async Task AdditionalAttemptClaimRejectsCurrentEvidenceDrift(string drift)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var p = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken);
        var envelope = e.Intent.Envelope with
        {
            RecoveryAuthorizationId = approved.AuthorizationId,
            CapabilityProvenanceId = string.Equals(drift, "envelope", StringComparison.Ordinal) ? "changed-capability" : e.Intent.Envelope.CapabilityProvenanceId
        };
        if (string.Equals(drift, "document", StringComparison.Ordinal))
            await f.Library.SaveAsync(new DocumentSaveRequest(e.Document.Id, e.Document.Title, e.Document.Text + "edited", e.Document.ConcurrencyVersion, DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken);
        if (string.Equals(drift, "project", StringComparison.Ordinal))
            Assert.False(await service.TryClaimAsync(approved, ClaimRecovery(e, approved), envelope, "wrong-project", "USD", 2, 3, TestContext.Current.CancellationToken));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryClaimAsync(approved, ClaimRecovery(e, approved), envelope,
                e.Intent.ProjectId, "USD", 2, string.Equals(drift, "price", StringComparison.Ordinal) ? 4 : 3, TestContext.Current.CancellationToken));
        var state = await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, approved.IdempotencyKey, TestContext.Current.CancellationToken);
        Assert.False(state!.SubmissionAttempted);
    }

    [Fact]
    public async Task StaleFinancialPreviewAndCancelledApprovalCreateNoChildOrReservation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var p = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).SaveApprovedAsync(e.OldSpend, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken));
        var fresh = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApproveAsync(Confirmation(fresh), cancelled.Token));
        using var context = f.CreateDbContext();
        Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoveryChainCannotForkOrLowerRetainedBudgetAfterInterruptedAttempt()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var p = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken));
        var claim = ClaimRecovery(e, approved);
        Assert.True(await service.TryClaimAsync(approved, claim, e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId }, e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        // Simulate restart after dispatch with no outcome: old and child uncertainty remain durable.
        var restarted = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var next = e.Intent with { PreviousIdempotencyKey = approved.IdempotencyKey };
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken));
        e.Budget.UnderreportFloor = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(next, 3, TestContext.Current.CancellationToken));
        e.Budget.UnderreportFloor = false;
        var second = await restarted.PrepareAsync(next, 3, TestContext.Current.CancellationToken);
        Assert.Equal(claim.LocalAttemptId, second.PreviousState.LocalAttemptId);
        Assert.True(second.PreviousState.UnresolvedSubmission);
        Assert.Equal(e.Previous, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    private static GoogleRecoveryBudgetReview Budget() => new(250, 1000, 80, new string('a', 64));

    [Theory]
    [InlineData("budget-missing")]
    [InlineData("budget-expired")]
    [InlineData("budget-scope")]
    [InlineData("predecessor-missing")]
    [InlineData("predecessor-document")]
    [InlineData("predecessor-payload")]
    public async Task RecoveryCannotAdmitCallerInventedBudgetOrPredecessor(string failure)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        if (string.Equals(failure, "budget-missing", StringComparison.Ordinal)) e.Budget.Missing = true;
        if (string.Equals(failure, "budget-expired", StringComparison.Ordinal)) e.Budget.Expired = true;
        if (string.Equals(failure, "budget-scope", StringComparison.Ordinal)) e.Budget.WrongScope = true;
        if (string.Equals(failure, "predecessor-missing", StringComparison.Ordinal)) e.Association.Value = null;
        if (string.Equals(failure, "predecessor-document", StringComparison.Ordinal)) e.Association.Value = e.Association.Value! with { DocumentId = Guid.NewGuid() };
        if (string.Equals(failure, "predecessor-payload", StringComparison.Ordinal)) e.Association.Value = e.Association.Value! with { OriginalPayloadSha256 = new string('c', 64) };
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("PRIVATE_RECOVERY_TEXT_SENTINEL", error.Message, StringComparison.Ordinal);
        Assert.Equal(e.Previous, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoveryBindsAdmissionDigestAtApprovalAndRevocationAtClaim()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var p = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var original = e.Association.Value!;
        e.Association.Value = original with { AdmissionSha256 = new string('c', 64) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken));
        e.Association.Value = original;
        p = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var a = await service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken);
        e.Association.Value = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryClaimAsync(a, ClaimRecovery(e, a), e.Intent.Envelope with { RecoveryAuthorizationId = a.AuthorizationId },
            e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        var child = await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, a.IdempotencyKey, TestContext.Current.CancellationToken);
        Assert.False(child!.SubmissionAttempted);
    }

    [Fact]
    public async Task LegacyUnknownMetadataRemainsNullAndRequiresIndependentAssociation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        using (var context = f.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("UPDATE google_generation_queue_states SET UnresolvedSubmission=0,LocalAttemptId=NULL,DiagnosticCode=NULL,PayloadSha256=NULL,PayloadBytes=NULL,OutcomeObservedAtUnixMilliseconds=NULL", TestContext.Current.CancellationToken);
        var store = new GoogleGenerationPersistedQueueStateStore(f);
        var legacy = (await store.LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken))!;
        e.Association.Value = e.Association.Value! with { HistoricalStateSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacy))) };
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var p = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var a = await service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken);
        Assert.Null(p.PreviousState.LocalAttemptId); Assert.Null(p.PreviousState.PayloadSha256);
        Assert.Equal(legacy, await store.LoadAsync(legacy.AccountId, legacy.OperationStableId, legacy.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.NotEqual(legacy.IdempotencyKey, a.IdempotencyKey, StringComparer.Ordinal);
    }
    private static GoogleRecoveryApprovalConfirmation Confirmation(GoogleRecoveryPreview p) => new(p.PreviewId, p.ReviewSha256, p.AdditionalMaximumMinorUnits, true, true);
    private static GoogleGenerationPersistedQueueState ClaimRecovery(RecoveryEvidence e, GoogleRecoveryApprovedAttempt a) =>
        new(e.Previous.AccountId, e.Previous.OperationStableId, a.IdempotencyKey, true, null, true, Guid.NewGuid().ToString("N"),
            "google-dispatch-claimed", e.Intent.Envelope.CompiledPayloadSha256, e.Intent.Envelope.CompiledPayloadBytes, 1);

    private static async Task<RecoveryEvidence> RecoveryEvidenceAsync(Fixture f)
    {
        var document = await f.Library.CreateAsync("Synthetic held narration", "PRIVATE_RECOVERY_TEXT_SENTINEL", TestContext.Current.CancellationToken).ConfigureAwait(false);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = document.Text }, voice = new { languageCode = "en-US", name = "synthetic-voice" }, audioConfig = new { audioEncoding = "MP3" } });
        var envelope = new GoogleGenerationSubmissionEnvelope("synthetic-account", "synthetic-credential-reference", "synthetic-capability", "synthetic-pricing", 42,
            "synthetic-voice", "MP3", Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);
        var store = new GoogleGenerationPersistedQueueStateStore(f);
        var initial = new GoogleGenerationPersistedQueueState("synthetic-account", GoogleGenerationProvider.SynthesizeOperationStableId, "synthetic-held-key", false, null);
        await store.SaveAsync(initial, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var claim = initial with
        {
            SubmissionAttempted = true,
            UnresolvedSubmission = true,
            LocalAttemptId = Guid.NewGuid().ToString("N"),
            DiagnosticCode = "google-dispatch-claimed",
            PayloadSha256 = envelope.CompiledPayloadSha256,
            PayloadBytes = payload.Length,
            OutcomeObservedAtUnixMilliseconds = 1
        };
        Assert.True(await store.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        var oldSpend = GoogleGenerationSpendAuthorization.Create(envelope, "USD", 2, 3, 3);
        await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).SaveApprovedAsync(oldSpend, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var intent = new GoogleRecoveryIntent(document.Id, document.CurrentRevisionId!.Value, "synthetic-project", initial.IdempotencyKey, envelope, "USD", 2, 3, payload);
        var predecessor = new GoogleRecoveryPredecessorEvidence("synthetic-logical-narration", initial.AccountId, initial.OperationStableId,
            initial.IdempotencyKey, Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(claim))), intent.ProjectId,
            document.Id, document.CurrentRevisionId.Value, Hash(document.Text), envelope.CompiledPayloadSha256, new string('b', 64), DateTimeOffset.UtcNow.AddHours(1), 2, 6);
        return new(document, intent, claim, oldSpend, new(f), new() { Value = predecessor });
    }

    private sealed record RecoveryEvidence(DocumentSnapshot Document, GoogleRecoveryIntent Intent,
        GoogleGenerationPersistedQueueState Previous, GoogleGenerationSpendAuthorization OldSpend,
        SyntheticRecoveryBudget Budget, SyntheticRecoveryAssociation Association);

    private sealed class SyntheticRecoveryAssociation : IGoogleRecoveryPredecessorEvidenceResolver
    {
        public GoogleRecoveryPredecessorEvidence? Value { get; set; }
        public Task<GoogleRecoveryPredecessorEvidence?> ResolveAsync(string accountId, string operationId, string historicalKey,
            string exactHistoricalStateSha256, CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }

    private sealed class SyntheticRecoveryBudget(Fixture fixture) : IGoogleRecoveryBudgetEvidenceResolver
    {
        public bool UnderreportFloor { get; set; }
        public bool Missing { get; set; }
        public bool Expired { get; set; }
        public bool WrongScope { get; set; }
        public async Task<GoogleRecoveryBudgetEvidence?> ResolveAsync(string accountId, string projectId, string currency, int scale,
            string currentFinancialStateSha256, CancellationToken cancellationToken = default)
        {
            if (Missing) return null;
            using var context = fixture.CreateDbContext();
            var rows = await context.Database.SqlQueryRaw<long>("SELECT RetainedFloorMinorUnits AS Value FROM google_recovery_scopes").ToArrayAsync(cancellationToken).ConfigureAwait(false);
            long floor = rows.Length == 0 || UnderreportFloor ? 250 : rows[0];
            return new(accountId, WrongScope ? "wrong-project" : projectId, currency, scale, currentFinancialStateSha256,
                Budget() with { RetainedCommittedMinorUnits = floor, RemainingAllocationMinorUnits = 80 - (floor - 250) },
                Expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1));
        }
    }
}
