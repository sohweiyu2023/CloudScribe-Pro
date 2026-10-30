using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Theory]
    [InlineData("off")]
    [InlineData("wrong-object")]
    [InlineData("attached")]
    [InlineData("withdrawn-after-claim")]
    public async Task ActualUiFactoryAndDatabaseEnforceOneResponseCaptureBeforeDiagnosticClaim(string mode)
    {
        using var f = new Fixture();
        var (articles, pending) = await f.InitializeDiagnosticSuccessorAsync();
        var snapshot = pending.Snapshot;
        var request = snapshot.ProviderRequest;
        using var capture = new GooglePrivateErrorDiagnostics(f.CaptureRoot, TimeProvider.System);
        var binding = new PrivateErrorCaptureBinding(f.Selection.DocumentId, f.Selection.RevisionId!.Value, 42,
            request.AccountId, f.Selection.ProjectStableId, f.Selection.VoiceStableId, "synthetic-credential", pending.SubmissionEnvelope.CompiledPayloadSha256);
        AttachSyntheticCapture(mode, capture, binding, request);
        var credential = new CaptureGateCredential();
        var handler = new CaptureGateHandler();
        using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, credential, new Uri("https://texttospeech.googleapis.com"), privateDiagnostics: capture);
        var account = new GoogleGenerationAccount(request.AccountId, "synthetic-credential", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global");
        var capability = new GoogleCapabilitySnapshot(request.AccountId, "synthetic-capability", DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1), new HashSet<string>(StringComparer.Ordinal) { f.Selection.VoiceStableId }, new HashSet<string>(StringComparer.Ordinal) { "MP3" }, 5000);
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var authorization = await spend.LoadApprovedAsync(pending.SubmissionEnvelope, TestContext.Current.CancellationToken);
        Assert.NotNull(authorization);
        var queueStore = new GoogleGenerationPersistedQueueStateStore(f);
        var context = GoogleGenerationUiExecutionContextFactory.Create(new(transport, account, capability, authorization,
            "synthetic-pricing", 42, "USD", 2, 1, snapshot), queueStore, articles: articles);
        Task<GoogleGenerationQueueOutcome> RunAsync() => context.Coordinator.ProcessPersistedTransitionAsync(snapshot.UiSelection,
            true, true, true, true, request, snapshot.AdmittedTrust, snapshot.PreviousState, snapshot.CurrentState,
            GoogleGenerationReconciliationResolutionEvidence.None, true, true, true, true, TestContext.Current.CancellationToken);

        if (mode is "off" or "wrong-object")
        {
            await Assert.ThrowsAsync<PrivateDiagnosticNotReadyException>(RunAsync);
            Assert.Equal(0, credential.Calls); Assert.Equal(0, handler.Calls);
            await AssertCaptureReservationAsync(f, 0);
        }
        else
        {
            if (string.Equals(mode, "withdrawn-after-claim", StringComparison.Ordinal)) credential.BeforeReturn = capture.Disarm;
            if (credential.BeforeReturn is not null)
            {
                await Assert.ThrowsAsync<PrivateDiagnosticNotReadyException>(RunAsync);
                var held = await queueStore.LoadAsync(request.AccountId, request.OperationStableId, request.IdempotencyKey, TestContext.Current.CancellationToken);
                Assert.True(held!.UnresolvedSubmission);
                Assert.Equal(0, handler.Calls);
            }
            else
            {
                _ = await RunAsync();
                await capture.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
                Assert.Contains("unfamiliar", Assert.Single(capture.ReadRecords()).Message, StringComparison.Ordinal);
                Assert.Equal(1, handler.Calls);
                var saved = await queueStore.LoadAsync(request.AccountId, request.OperationStableId, request.IdempotencyKey, TestContext.Current.CancellationToken);
                Assert.Equal("google-http-400", saved!.DiagnosticCode); Assert.False(saved.UnresolvedSubmission);
            }
            await AssertCaptureReservationAsync(f, 1);
            await Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        }
        await AssertCaptureLineageAsync(f);
    }

    private static async Task AssertCaptureLineageAsync(Fixture f)
    {
        using var final = f.CreateDbContext();
        Assert.Equal(1, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts WHERE Consumed=1 AND ReservedMinorUnits=1").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(1, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_successor_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    private static void AttachSyntheticCapture(string mode, GooglePrivateErrorDiagnostics capture, PrivateErrorCaptureBinding binding, GenerationProviderRequest request)
    {
        if (string.Equals(mode, "off", StringComparison.Ordinal)) return;
        capture.Arm(binding, true);
        var attached = string.Equals(mode, "wrong-object", StringComparison.Ordinal)
            ? new GenerationProviderRequest(request.ProviderStableId, request.OperationStableId, request.AccountId, request.IdempotencyKey, request.CompiledPayload, request.OutputFormat)
            : request;
        capture.Attach(attached, binding);
    }

    private static async Task AssertCaptureReservationAsync(Fixture f, int consumed)
    {
        using var db = f.CreateDbContext();
        Assert.Equal(consumed, await db.Database.SqlQueryRaw<int>("SELECT Consumed AS Value FROM chirp_article_successor_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT ReservedMinorUnits AS Value FROM chirp_article_successor_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    private sealed class CaptureGateCredential : ITransientCredentialResolver
    {
        public int Calls { get; private set; }
        public Action? BeforeReturn { get; set; }
        public ValueTask<string> ResolveAccessTokenAsync(string reference, CancellationToken token)
        { Calls++; BeforeReturn?.Invoke(); return ValueTask.FromResult("SYNTHETIC_SECRET_ACCESS_TOKEN"); }
    }

    private sealed class CaptureGateHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"An unfamiliar provider explanation remains useful.\"}}") });
        }
    }

    private sealed partial class Fixture
    {
        public string CaptureRoot => Path.Combine(_root, "private-synthetic");
        public async Task<(ChirpArticleService Articles, GoogleGenerationProductionSubmissionState Pending)> InitializeDiagnosticSuccessorAsync()
        {
            await InitializeChirpAsync().ConfigureAwait(false);
            var articles = CreateArticles();
            var spend = new GoogleGenerationSpendAuthorizationStore(this, TimeProvider.System, articles: articles);
            var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
            var prep = new ChirpArticlePreparationService(articles, ChirpCompiler, owner, TimeProvider.System);
            var review = await prep.PrepareAsync(Selection, 1, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await prep.ApproveAsync(Selection, ChirpConfirm(review), TestContext.Current.CancellationToken).ConfigureAwait(false);
            var parent = (await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
            var claim = parent.Snapshot.CurrentState with
            {
                SubmissionAttempted = true,
                UnresolvedSubmission = true,
                LocalAttemptId = Guid.NewGuid().ToString("N"),
                DiagnosticCode = "google-dispatch-claimed",
                PayloadSha256 = parent.SubmissionEnvelope.CompiledPayloadSha256,
                PayloadBytes = parent.SubmissionEnvelope.CompiledPayloadBytes,
                OutcomeObservedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            Assert.True(await articles.TryClaimAuthorizedAsync(parent.SubmissionEnvelope, Selection.ProjectStableId, "USD", 2, 1, claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
            await new GoogleGenerationPersistedQueueStateStore(this).SaveOutcomeAsync(claim with
            {
                UnresolvedSubmission = false,
                DiagnosticCode = "google-http-400",
                ErrorStatus = GoogleErrorStatus.InvalidArgument
            }, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var rows = await ReadCaptureRowsAsync("chirp_article_attempts").ConfigureAwait(false);
            var queues = await ReadCaptureRowsAsync("google_generation_queue_states").ConfigureAwait(false);
            string policyFile = Path.Combine(_root, "install", "chirp-article-admission-policy.json");
            var policy = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(policyFile))!;
            var grant = new ChirpArticleSuccessorGrant(Guid.NewGuid(), parent.SubmissionEnvelope.ChirpAuthorizationId!.Value,
                parent.SubmissionEnvelope.ChirpAuthorizationId.Value, policy.Articles.Single().Proposal.PlanSha256, 1,
                parent.Snapshot.ProviderRequest.IdempotencyKey, JsonHash(Assert.Single(rows)),
                JsonHash(queues.Single(x => Equals(x[2], parent.Snapshot.ProviderRequest.IdempotencyKey))), claim.LocalAttemptId!,
                "synthetic-credential", new string('d', 64), 1, 1);
            policy = policy with
            {
                SchemaVersion = 2,
                SourceApprovalSha256 = new string('e', 64),
                Budget = policy.Budget with
                {
                    ProjectCommittedMinorUnits = 149,
                    AllocationCommittedMinorUnits = 19,
                    QueueRows = await PinsAsync("google_generation_queue_states", 3).ConfigureAwait(false)
                },
                PriorArticleAttemptRows = await PinsAsync("chirp_article_attempts", 1).ConfigureAwait(false),
                PriorSuccessorAttemptRows = [],
                SuccessorGrant = grant,
                Articles = [policy.Articles.Single() with { AllowedSegments = [new(1, 1)] }],
                MaximumNewAttempts = 1,
                MaximumAdditionalReservationMinorUnits = 1
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy); File.WriteAllBytes(policyFile, bytes);
            File.WriteAllText(Path.Combine(_root, "install", "cloudscribe-install.json"), JsonSerializer.Serialize(new
            { Product = "CloudScribe Pro", AppSha256 = policy.AppSha256, ChirpArticlePolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));
            owner = new(new GoogleGenerationSpendAuthorizationStore(this, TimeProvider.System, articles: articles));
            prep = new(articles, ChirpCompiler, owner, TimeProvider.System);
            review = await prep.PrepareAsync(Selection, 1, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await prep.ApproveAsync(Selection, ChirpConfirm(review), TestContext.Current.CancellationToken).ConfigureAwait(false);
            return (articles, (await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!);
        }

        private async Task<object?[][]> ReadCaptureRowsAsync(string table)
        {
            using var db = CreateDbContext(); var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
            using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            List<object?[]> rows = [];
            while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
            { object[] row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row.Select(x => x is DBNull ? null : x).ToArray()); }
            return rows.ToArray();
        }
    }
}
