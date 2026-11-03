using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task DiagnosticNativeSerializerPinsMatchExactNestedBytesForCrossLanguageReadback()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var state = await SentenceDiagnosticAsync(f);
        var policy = state.Evidence.Admission.Policy!;
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(policy);
        Assert.Equal(ChirpPolicyValidation.Hash(JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(raw)!),
            Convert.ToHexStringLower(SHA256.HashData(raw)));
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        static string Hash(JsonElement element) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(element.GetRawText())));
        Assert.Equal(policy.DiagnosticEvidence!.ProposalSha256, Hash(root.GetProperty("Articles")[0].GetProperty("Proposal")));
        Assert.Equal(policy.DiagnosticEvidence.SuccessorGrantSha256, Hash(root.GetProperty("SuccessorGrant")));
        TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_NATIVE_POLICY_BASE64=" + Convert.ToBase64String(raw));
    }
    [Fact]
    public async Task TypedSentenceDiagnosticPreservesBothLineagesAndResumesOnlyOneNewCent()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var state = await SentenceDiagnosticAsync(f);
        var e = state.Evidence;
        string before = JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var review = await e.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        Assert.Equal(state.Parent.AuthorizationId, review.FailedParentAuthorizationId);
        var child = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        Assert.NotEqual(child.AuthorizationId, state.Parent.AuthorizationId);
        var restarted = DiagnosticService(f, e);
        var resumed = await restarted.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        Assert.Equal(child.AuthorizationId, resumed.ExistingAuthorizationId);
        Assert.Equal(child, await restarted.ApproveAsync(ChirpConfirmation(resumed), TestContext.Current.CancellationToken));
        Assert.Equal(before, JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts")));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_successor_attempts"));
        Assert.Equal(2, (await restarted.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SentenceDiagnosticCannotReserveWithoutItsRequiredGlobalAdmission()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var state = await SentenceDiagnosticAsync(f);
        var e = state.Evidence;
        foreach (var service in new[] {
            new ChirpArticleService(f, e.Admission, f.Paths, e.Clock),
            new ChirpArticleService(f, e.Admission, f.Paths, e.Clock, completionGate:
                new CompletionWorkflowGate(() => null, new DiagnosticAdmissionRunner(() => e.Admission.Policy!))) })
            await Assert.ThrowsAsync<CompletionGateException>(() => service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_successor_attempts"));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("consent")]
    [InlineData("zero-consent")]
    [InlineData("same-consent")]
    [InlineData("proposal")]
    [InlineData("grant")]
    [InlineData("capture")]
    [InlineData("whole-job")]
    [InlineData("uncertain")]
    [InlineData("broad-budget")]
    public async Task DiagnosticPurposeCannotRelaxSentenceOrFinancialGuards(string mutation)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var state = await SentenceDiagnosticAsync(f);
        var e = state.Evidence; var p = e.Admission.Policy!; var d = p.DiagnosticEvidence!;
        e.Admission.Policy = mutation switch
        {
            "schema" => p with { SchemaVersion = 2 },
            "consent" => p with { DiagnosticEvidence = d with { PrivateCaptureConsentSha256 = "" } },
            "zero-consent" => p with { DiagnosticEvidence = d with { PrivateCaptureConsentSha256 = new string('0', 64) } },
            "same-consent" => p with { DiagnosticEvidence = d with { PrivateCaptureConsentSha256 = p.SourceApprovalSha256 } },
            "proposal" => p with { DiagnosticEvidence = d with { ProposalSha256 = new string('a', 64) } },
            "grant" => p with { DiagnosticEvidence = d with { SuccessorGrantSha256 = new string('b', 64) } },
            "capture" => p with { DiagnosticEvidence = d with { RequiredCaptureMode = PrivateErrorCaptureMode.SelectedExplanation } },
            "whole-job" => p with { WholeArticleGeneration = new(Guid.NewGuid(), [e.Article.Proposal.PlanSha256]) },
            "broad-budget" => p with { Budget = p.Budget with { ProjectCeilingMinorUnits = p.Budget.ProjectCommittedMinorUnits + 2 } },
            _ => p,
        };
        if (string.Equals(mutation, "uncertain", StringComparison.Ordinal))
        {
            using var db = f.CreateDbContext();
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database,
                "UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE IdempotencyKey={0}",
                [state.Parent.IdempotencyKey], TestContext.Current.CancellationToken);
            e.Admission.Policy = p with { Budget = p.Budget with { QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3) } };
        }
        var error = await Record.ExceptionAsync(() => e.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken));
        Assert.True(error is InvalidDataException or InvalidOperationException);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_successor_attempts"));
    }

    private static async Task<ChirpSuccessorFixture> SentenceDiagnosticAsync(Fixture f)
    {
        var sentence = await SentenceEvidenceAsync(f).ConfigureAwait(false); var e = sentence.Evidence;
        int ordinal = e.Article.AllowedSegments[0].CoverageOrdinal;
        // Reproduce the independently admitted first-stage shape, not a whole-article grant bypass.
        e.Admission.Policy = e.Admission.Policy! with
        {
            SchemaVersion = 2,
            WholeArticleGeneration = null,
            Articles = [e.Article with { AllowedSegments = [new(ordinal, 1)] }],
            MaximumNewAttempts = 1,
            MaximumAdditionalReservationMinorUnits = 1
        };
        var intent = ChirpIntent(e, ordinal);
        var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var parent = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var claim = ChirpClaim(intent, parent);
        Assert.True(await e.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = parent.AuthorizationId },
            intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        var failed = claim with { DiagnosticCode = "google-http-400", ErrorStatus = GoogleErrorStatus.InvalidArgument, UnresolvedSubmission = false };
        await new GoogleGenerationPersistedQueueStateStore(f, e.Admission).SaveOutcomeAsync(failed, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var rows = await FinancialRowsAsync(f, "chirp_article_attempts").ConfigureAwait(false);
        var queues = await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false);
        var row = rows.Single(x => Equals(x[0], parent.AuthorizationId.ToString("N")));
        var grant = new ChirpArticleSuccessorGrant(Guid.NewGuid(), parent.AuthorizationId, parent.AuthorizationId,
            e.Article.Proposal.PlanSha256, ordinal, parent.IdempotencyKey, JsonHash(row), JsonHash(queues.Single(x => Equals(x[2], parent.IdempotencyKey))),
            claim.LocalAttemptId!, intent.Envelope.CredentialReferenceId, new string('d', 64), 1, 1);
        var p = e.Admission.Policy!;
        e.Admission.Policy = p with
        {
            SchemaVersion = 4,
            SourceApprovalSha256 = new string('e', 64),
            WholeArticleGeneration = null,
            SuccessorGrant = grant,
            DiagnosticEvidence = new(1, "unchanged-provider-evidence", ChirpPolicyValidation.Hash(e.Article.Proposal),
                ChirpPolicyValidation.Hash(grant), new string('c', 64), PrivateErrorCaptureMode.ProtectedResponseEnvelope),
            Budget = p.Budget with
            {
                ProjectCommittedMinorUnits = 152,
                AllocationCommittedMinorUnits = 22,
                ProjectCeilingMinorUnits = 153,
                AllocationCeilingMinorUnits = 23,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            },
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false),
            PriorSuccessorAttemptRows = [],
            Articles = [e.Article with { AllowedSegments = [new(ordinal, 1)] }],
            MaximumNewAttempts = 1,
            MaximumAdditionalReservationMinorUnits = 1,
        };
        e = e with { Service = DiagnosticService(f, e) };
        return new(e, intent, parent, failed, JsonHash(rows));
    }

    private static ChirpArticleService DiagnosticService(Fixture f, ChirpEvidence e) =>
        new(f, e.Admission, f.Paths, e.Clock, completionGate: new CompletionWorkflowGate(
            CompletionGatePayloadTests.Configuration, new DiagnosticAdmissionRunner(() => e.Admission.Policy!)));

    private sealed class DiagnosticAdmissionRunner(Func<ChirpArticleAdmissionPolicy> policy) : ICompletionGateCommandRunner
    {
        public void Run(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt)
        {
            Assert.Equal("check-diagnostic-admission", command);
            Assert.Equal(Guid.Empty, id);
            Assert.Equal(ChirpPolicyValidation.Hash(policy()), identity);
            Assert.Null(receipt);
        }
    }
}
