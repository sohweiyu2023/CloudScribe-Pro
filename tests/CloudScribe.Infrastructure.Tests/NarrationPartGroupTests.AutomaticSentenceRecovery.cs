using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ReviewedHistoricalStoppedRootUsesOnlyNewChildrenAndPreservesOldJobAndHolds()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        var firstReview = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var firstJob = await e.Service.StartWholeArticleAsync(WholeConfirmation(firstReview), TestContext.Current.CancellationToken);
        var failedRuntime = new RecoveryFakeRuntime(f, e) { FailureMode = "generic" };
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.RunWholeArticleAsync(firstJob.JobId, failedRuntime, token: TestContext.Current.CancellationToken));
        var stopped = await e.Service.ReadWholeArticleJobAsync(firstJob.JobId, TestContext.Current.CancellationToken);
        e = await AdmitReviewedHistoricalRecoveryAsync(f, e);
        TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_BOUNDED_RECOVERY_POLICY_BASE64="
            + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e.Admission.Policy)));
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var recoveryRuntime = new RecoveryFakeRuntime(f, e);
        var done = await e.Service.RunWholeArticleAsync(job.JobId, recoveryRuntime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, done.State);
        Assert.Equal(1, failedRuntime.RootFailures);
        Assert.Equal(0, recoveryRuntime.RootFailures);
        Assert.Equal(2, recoveryRuntime.Children.Count);
        Assert.Equal(stopped, await e.Service.ReadWholeArticleJobAsync(firstJob.JobId, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "google_generation_sentence_rejections"));
    }

    private static async Task<ChirpEvidence> AdmitReviewedHistoricalRecoveryAsync(Fixture f, ChirpEvidence e)
    {
        var attempt = Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts").ConfigureAwait(false));
        var queue = (await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false)).Single(x => Equals(x[2], attempt[7]));
        var envelope = System.Text.Json.JsonSerializer.Deserialize<GoogleGenerationSubmissionEnvelope>((string)attempt[9]!)!;
        var p = e.Admission.Policy!;
        e.Admission.Policy = p with
        {
            SourceApprovalSha256 = new string('d', 64),
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false),
            WholeArticleGeneration = p.WholeArticleGeneration! with { GrantId = Guid.NewGuid() },
            SentenceRecovery = p.SentenceRecovery! with
            {
                GrantId = Guid.NewGuid(),
                ReviewedHistoricalRejections =
                [new(Guid.ParseExact((string)attempt[0]!, "N"), JsonHash(attempt), JsonHash(queue), envelope.CompiledPayloadSha256,
                    envelope.CompiledPayloadBytes, Guid.NewGuid(), new string('e', 64), new string('f', 64))]
            },
            Budget = p.Budget with
            {
                ProjectCommittedMinorUnits = p.Budget.ProjectCommittedMinorUnits + 1,
                AllocationCommittedMinorUnits = p.Budget.AllocationCommittedMinorUnits + 1,
                ProjectCeilingMinorUnits = p.Budget.ProjectCeilingMinorUnits + 1,
                AllocationCeilingMinorUnits = p.Budget.AllocationCeilingMinorUnits + 1,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            }
        };
        return e with { Service = new(f, e.Admission, f.Paths, e.Clock) };
    }

    [Fact]
    public async Task BothRejectedFirstLevelChildrenUseAtMostSixAttemptsAndFourRealAcceptedLeaves()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new RecoveryFakeRuntime(f, e) { FailureMode = "both-depth1" };
        var done = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, done.State);
        Assert.Equal(6, runtime.Children.Count);
        Assert.Equal(4, (await FinancialRowsAsync(f, "chirp_article_split_receipts")).Count);
        Assert.Equal(6, (await FinancialRowsAsync(f, "chirp_article_split_attempts")).Count);
        Assert.Equal(1, runtime.RootFailures);
    }

    [Fact]
    public async Task ThirdDepthIsNeverProposedOrDispatchedAfterConfirmedRejections()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new RecoveryFakeRuntime(f, e) { FailureMode = "all-too-long" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(2, runtime.Children.Count);
        Assert.DoesNotContain(runtime.Children, x => x.Depth > 2);
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_split_attempts")).Count);
        Assert.False(runtime.Exported);
    }

    [Fact]
    public async Task AcceptedFirstChildSurvivesProcessInterruptionAndRestartWithoutAnotherHttpOutcome()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        var (job, first) = await CreateAcceptedChildCheckpointAsync(f, e);
        var persisted = await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Executing, persisted.State);
        string receipt = JsonHash(Assert.Single(await FinancialRowsAsync(f, "chirp_article_split_receipts")));
        var restarted = e with { Service = new(f, e.Admission, f.Paths, e.Clock) };
        var runtime = new RecoveryFakeRuntime(f, restarted);
        var reopened = await restarted.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var same = await restarted.Service.StartWholeArticleAsync(WholeConfirmation(reopened), TestContext.Current.CancellationToken);
        Assert.Equal(job.JobId, same.JobId);
        Assert.Equal(job.ExpiresAtUtc, same.ExpiresAtUtc);
        Assert.Equal(ChirpWholeArticleState.Complete, (await restarted.Service.RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken)).State);
        Assert.Single(runtime.Children);
        Assert.DoesNotContain(runtime.Children, x => x.AuthorizationId == first.AuthorizationId);
        Assert.Equal(0, runtime.RootFailures);
        Assert.Equal(receipt, JsonHash((await FinancialRowsAsync(f, "chirp_article_split_receipts")).Single(x => Equals(x[0], first.AuthorizationId.ToString("N")))));
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_split_attempts")).Count);
    }

    [Fact]
    public async Task NewPolicyAndGrantInSameWindowCannotForgetAlreadyReservedRecoveryUsage()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        _ = await CreateAcceptedChildCheckpointAsync(f, e);
        await RepinRecoveryPolicyWithoutResetAsync(f, e).ConfigureAwait(true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ChirpArticleService(f, e.Admission, f.Paths, e.Clock)
            .PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("allowance", error.Message, StringComparison.Ordinal);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_split_attempts").ConfigureAwait(true));
    }

    private static async Task<(ChirpWholeArticleJob Job, ChirpArticleRecoveryChild Child)> CreateAcceptedChildCheckpointAsync(Fixture f, ChirpEvidence e)
    {
        var token = TestContext.Current.CancellationToken;
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, token).ConfigureAwait(false);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), token).ConfigureAwait(false);
        // Invoke the actual production checkpoint writer, then stop driving the runtime
        // after an actual durable child save. No fatal exception or real process kill is used.
        var transition = typeof(ChirpArticleService).GetMethod("TransitionWholeArticleAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        job = await ((Task<ChirpWholeArticleJob>)transition.Invoke(e.Service,
            [job, ChirpWholeArticleState.Executing, 1, "article.request-starting", token, null, false])!).ConfigureAwait(false);
        var runtime = new RecoveryFakeRuntime(f, e);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GenerateAsync(job, 1, token)).ConfigureAwait(false);
        var parent = Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts").ConfigureAwait(false));
        var children = await e.Service.PrepareRecoveryChildrenAsync(job.JobId, Guid.ParseExact((string)parent[0]!, "N"), token).ConfigureAwait(false);
        await runtime.GenerateRecoveryAsync(job, children[0], token).ConfigureAwait(false);
        return (job, children[0]);
    }

    private static async Task RepinRecoveryPolicyWithoutResetAsync(Fixture f, ChirpEvidence e)
    {
        var p = e.Admission.Policy!;
        var successors = (await FinancialRowsAsync(f, "chirp_article_successor_attempts").ConfigureAwait(false))
            .Concat(await FinancialRowsAsync(f, "chirp_article_split_attempts").ConfigureAwait(false)).ToArray();
        e.Admission.Policy = p with
        {
            SourceApprovalSha256 = new string('e', 64),
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false),
            PriorSuccessorAttemptRows = successors.Select(x => new RecoveryFinancialRowPin(JsonHash(x.Take(1).ToArray()), JsonHash(x))).ToArray(),
            SentenceRecovery = p.SentenceRecovery! with
            {
                GrantId = Guid.NewGuid(),
                MaximumChildAttemptsPerRange = 2,
                MaximumChildAttemptsPerJob = 2,
                MaximumChildAttemptsTotal = 2,
                MaximumAdditionalMinorUnits = 2
            },
            Budget = p.Budget with
            {
                ProjectCommittedMinorUnits = p.Budget.ProjectCommittedMinorUnits + 2,
                AllocationCommittedMinorUnits = p.Budget.AllocationCommittedMinorUnits + 2,
                ProjectCeilingMinorUnits = p.Budget.ProjectCeilingMinorUnits + 2,
                AllocationCeilingMinorUnits = p.Budget.AllocationCeilingMinorUnits + 2,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            }
        };
    }

    [Fact]
    public async Task AdmittedKnownSentenceFailureCreatesTwoRealChildrenAndOrderedCoverageWithoutRootReplay()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.True(review.MaximumRecoveryRequests >= 2);
        Assert.True(review.MaximumRecoveryMinorUnits >= 2);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new RecoveryFakeRuntime(f, e);
        var done = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, done.State);
        Assert.Equal(1, runtime.RootFailures);
        Assert.Equal(2, runtime.Children.Count);
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_split_attempts")).Count);
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_split_receipts")).Count);
        Assert.Single(await FinancialRowsAsync(f, "google_generation_sentence_rejections"));
        Assert.DoesNotContain(await FinancialRowsAsync(f, "chirp_article_receipts"), x => Equals(x[1], 1L));
        Assert.True((await e.Service.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).Complete);
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        int calls = runtime.Calls;
        Assert.Equal(done, await restarted.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(calls, runtime.Calls);
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("uncertain")]
    [InlineData("child-uncertain")]
    public async Task RejectionAloneAndUncertainOutcomesNeverAuthorizeAutomaticChildren(string failure)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new RecoveryFakeRuntime(f, e) { FailureMode = failure };
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        var stopped = await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Stopped, stopped.State);
        Assert.Equal(string.Equals(failure, "child-uncertain", StringComparison.Ordinal) ? 1 : 0, runtime.Children.Count);
        int calls = runtime.Calls;
        Assert.Equal(stopped, await new ChirpArticleService(f, e.Admission, f.Paths, e.Clock)
            .RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(calls, runtime.Calls);
        Assert.False(runtime.Exported);
    }

    private static async Task<ChirpEvidence> AutomaticRecoveryEvidenceAsync(Fixture f, bool compactSentenceSource = false)
    {
        var e = await ChirpEvidenceAsync(f, 8, true, compactSentenceSource).ConfigureAwait(false);
        var proposal = ChirpArticlePlanCompiler.CompileSentences(e.Document, e.Article.Proposal.LanguageCode,
            e.Article.Proposal.VoiceName, inputFormat: new(1));
        var article = e.Article with
        {
            Proposal = proposal,
            AllowedSegments = proposal.Parts.Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray(),
            Lineage = ChirpPlanLineageMapper.Map(e.Document.Text, proposal.Parts.Select(x => x.Range).ToArray(), e.Article.Lineage.History)
        };
        int maximum = article.AllowedSegments.Count + 6;
        var p = e.Admission.Policy!;
        e.Admission.Policy = p with
        {
            SchemaVersion = 3,
            PriorSuccessorAttemptRows = [],
            Articles = [article],
            MaximumNewAttempts = maximum,
            MaximumAdditionalReservationMinorUnits = maximum,
            WholeArticleGeneration = new(Guid.NewGuid(), [proposal.PlanSha256]),
            SentenceRecovery = new(1, Guid.NewGuid(), ChirpBoundedSentenceSplit.Version, 2, 6, 6, 6, 6, []),
            Budget = p.Budget with
            {
                ProjectCeilingMinorUnits = p.Budget.ProjectCommittedMinorUnits + maximum,
                AllocationCeilingMinorUnits = p.Budget.AllocationCommittedMinorUnits + maximum
            }
        };
        return e with { Article = article };
    }

    private sealed class RecoveryFakeRuntime(Fixture fixture, ChirpEvidence evidence) : IChirpWholeArticleRuntime
    {
        public string FailureMode { get; init; } = "known";
        public int RootFailures { get; private set; }
        public int Calls { get; private set; }
        public bool Exported { get; private set; }
        public List<ChirpArticleRecoveryChild> Children { get; } = [];
        public Action<ChirpArticleRecoveryChild, CloudScribe.Providers.Abstractions.GenerationProviderRequest>? ObserveRecoveryProvider { get; init; }

        public async Task GenerateAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken token)
        {
            Calls++;
            var intent = ChirpIntent(evidence, ordinal);
            var review = await evidence.Service.PrepareAsync(intent, token).ConfigureAwait(false);
            var approved = await evidence.Service.ApproveWholeArticleRequestAsync(job.JobId, review.PreviewId, token).ConfigureAwait(false);
            if (ordinal == 1)
            {
                RootFailures++;
                await RejectAsync(intent, approved, string.Equals(FailureMode, "uncertain", StringComparison.Ordinal),
                    !string.Equals(FailureMode, "generic", StringComparison.Ordinal), token).ConfigureAwait(false);
                throw new InvalidOperationException("Synthetic provider rejected this exact request.");
            }
            var accepted = await AcceptChirpAsync(fixture, evidence.Service, intent, ordinal, approved).ConfigureAwait(false);
            await evidence.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, token).ConfigureAwait(false);
        }

        public async Task GenerateRecoveryAsync(ChirpWholeArticleJob job, ChirpArticleRecoveryChild child, CancellationToken token)
        {
            Calls++; Children.Add(child);
            var source = await evidence.Service.ResolveRecoveryChildAsync(child, token).ConfigureAwait(false);
            var wire = ChirpArticlePlanCompiler.CompilePart(evidence.Document.Text, source.Proposal, source.Part);
            var intent = new ChirpRequestIntent(evidence.Document.Id, child.CoverageOrdinal, "synthetic-project", ChirpEnvelope(wire), wire.Payload, "USD", 2, 1);
            var approved = await evidence.Service.ApproveRecoveryChildAsync(child, intent, token).ConfigureAwait(false);
            if (string.Equals(FailureMode, "child-uncertain", StringComparison.Ordinal))
            {
                await RejectAsync(intent, approved, true, false, token).ConfigureAwait(false);
                throw new IOException("Synthetic unknown child outcome.");
            }
            if (string.Equals(FailureMode, "all-too-long", StringComparison.Ordinal)
                || string.Equals(FailureMode, "below-depth3", StringComparison.Ordinal) && child.Depth < 3
                || string.Equals(FailureMode, "both-depth1", StringComparison.Ordinal) && child.Depth == 1)
            {
                await RejectAsync(intent, approved, false, true, token).ConfigureAwait(false);
                throw new InvalidOperationException("Synthetic confirmed child sentence length rejection.");
            }
            var accepted = await AcceptChirpAsync(fixture, evidence.Service, intent, child.CoverageOrdinal, approved,
                submitted => ObserveRecoveryProvider?.Invoke(child, submitted)).ConfigureAwait(false);
            await evidence.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, token).ConfigureAwait(false);
        }

        private async Task RejectAsync(ChirpRequestIntent intent, ChirpApprovedAttempt approved, bool uncertain, bool known, CancellationToken token)
        {
            var claim = ChirpClaim(intent, approved);
            Assert.True(await evidence.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId },
                intent.ProjectId, "USD", 2, 1, claim, token).ConfigureAwait(false));
            await new GoogleGenerationPersistedQueueStateStore(fixture, evidence.Admission).SaveOutcomeAsync(claim with
            {
                UnresolvedSubmission = uncertain,
                DiagnosticCode = uncertain ? "google-transport-unknown" : "google-http-400",
                ErrorStatus = uncertain ? GoogleErrorStatus.NotProvided : GoogleErrorStatus.InvalidArgument,
                ValidationHint = !uncertain && known ? GoogleValidationMessageHint.SentenceLengthReported : GoogleValidationMessageHint.NotProvided
            }, token).ConfigureAwait(false);
        }

        public async Task<ChirpWholeArticleOutput> ExportAsync(ChirpWholeArticleJob job, CancellationToken token)
        {
            Assert.True((await evidence.Service.ProgressAsync(job.DocumentId, token).ConfigureAwait(false)).Complete);
            Exported = true;
            string path = Path.Combine(fixture.Root, "synthetic-recovered-whole.mp3");
            byte[] bytes = new byte[128]; await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
            return new(path, bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        }
    }
}
