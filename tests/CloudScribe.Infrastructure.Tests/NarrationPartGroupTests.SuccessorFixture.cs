using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<SuccessorFixture> StageSuccessorAsync(Fixture f, RecoveryEvidence e)
    {
        e.Association.Value = e.Association.Value! with { MaximumNewAttempts = 1, MaximumAdditionalReservationMinorUnits = 3 };
        var (admission, budget) = await CreateInstalledBudgetAsync(f, e).ConfigureAwait(false);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var prior = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var claim = ClaimRecovery(e, prior);
        Assert.True(await service.TryClaimAuthorizedAsync(e.Intent.Envelope with { RecoveryAuthorizationId = prior.AuthorizationId },
            e.Intent.ProjectId, "USD", 2, 3, claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        var failed = claim with
        {
            UnresolvedSubmission = false,
            ErrorStatus = GoogleErrorStatus.InvalidArgument,
            DiagnosticCode = "google-http-400",
            OutcomeObservedAtUnixMilliseconds = 2
        };
        await new GoogleGenerationPersistedQueueStateStore(f).SaveOutcomeAsync(failed, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var scope = Assert.Single(await FinancialRowsAsync(f, "google_recovery_scopes").ConfigureAwait(false));
        var transition = new RecoveryAllocationTransition(JsonHash(scope), 80, 83, 70, new string('c', 64));
        var link = new RecoverySuccessorGrant(prior.AuthorizationId, prior.IdempotencyKey, JsonHash(failed),
            e.Association.Value.AdmissionSha256, await FinancialHashAsync(f).ConfigureAwait(false), new string('c', 64), transition);
        var successor = RehashSuccessor(e.Association.Value with { Successor = link, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) });
        var previousPolicy = (await admission.LoadVerifiedPolicyAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
        var baseline = new RecoveryBudgetBaseline(253, 1000, 13, 83, new string('d', 64),
            await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false),
            await RowPinsAsync(f, "google_generation_spend_authorizations", 1).ConfigureAwait(false),
            await RowPinsAsync(f, "google_recovery_attempts", 1).ConfigureAwait(false), []);
        var policy = previousPolicy with { SchemaVersion = 3, ExpiresAtUtc = successor.ExpiresAtUtc, Budget = baseline, Predecessors = [successor] };
        WriteSuccessorPolicy(f, policy);
        return new(prior, failed, policy, admission, budget);
    }

    private static GoogleRecoveryPredecessorEvidence RehashSuccessor(GoogleRecoveryPredecessorEvidence e) =>
        e with { AdmissionSha256 = JsonHash(e with { AdmissionSha256 = "" }) };

    private static void WriteSuccessorPolicy(Fixture f, RecoveryAdmissionPolicy policy)
    {
        string install = Path.Combine(f.Root, "synthetic-install");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy);
        File.WriteAllBytes(Path.Combine(install, "recovery-admission-policy.json"), bytes);
        File.WriteAllText(Path.Combine(install, "cloudscribe-install.json"), JsonSerializer.Serialize(new
        { Product = "CloudScribe Pro", policy.AppSha256, RecoveryPolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));
    }

    private sealed record SuccessorFixture(GoogleRecoveryApprovedAttempt Prior, GoogleGenerationPersistedQueueState Failed,
        RecoveryAdmissionPolicy Policy, InstalledRecoveryAdmissionStore Admission, InstalledRecoveryBudgetEvidenceResolver Budget)
    {
        public GoogleExplicitRecoveryService Service(Fixture f) => new(f, TimeProvider.System, Budget, Admission);
        public GoogleRecoveryIntent Intent(RecoveryEvidence e) => e.Intent with { PreviousIdempotencyKey = Prior.IdempotencyKey };
    }
}
