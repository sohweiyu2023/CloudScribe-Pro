using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ReviewedBuildRepairContinuesSameStoppedFamilyWithoutRootOrRejectedChildReplay()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, failed, old) = await StoppedRecoveryCheckpointAsync(f);
        await AdmitRecoveryContinuationAsync(f, e, stopped, failed, old);
        TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_RECOVERY_CONTINUATION_POLICY_BASE64="
            + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e.Admission.Policy)));
        var service = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        var review = await service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var same = await service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Equal(stopped.JobId, same.JobId); Assert.Equal(stopped.ExpiresAtUtc, same.ExpiresAtUtc);
        Assert.Equal(stopped.MaximumRecoveryRequests, same.MaximumRecoveryRequests);
        var runtime = new RecoveryFakeRuntime(f, e with { Service = service });
        Assert.Equal(ChirpWholeArticleState.Complete,
            (await service.RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, runtime.RootFailures);
        Assert.DoesNotContain(runtime.Children, x => x.AuthorizationId == failed.AuthorizationId);
        Assert.Equal(3, runtime.Children.Count);
        Assert.Equal(4, (await FinancialRowsAsync(f, "chirp_article_split_attempts")).Count);
        Assert.Equal(3, (await FinancialRowsAsync(f, "chirp_article_split_receipts")).Count);
        Assert.Equal(same.JobId, (await service.ReadWholeArticleJobAsync(same.JobId, TestContext.Current.CancellationToken)).JobId);
        Assert.Equal(ChirpWholeArticleState.Complete,
            (await new ChirpArticleService(f, e.Admission, f.Paths, e.Clock).RunWholeArticleAsync(same.JobId, runtime,
                token: TestContext.Current.CancellationToken)).State);
        Assert.Equal(3, runtime.Children.Count);
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("deadline")]
    [InlineData("grant")]
    public async Task ReviewedContinuationRejectsChangedCheckpointDeadlineOrGrantBeforeAnyNewChild(string change)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, failed, old) = await StoppedRecoveryCheckpointAsync(f);
        await AdmitRecoveryContinuationAsync(f, e, stopped, failed, old);
        var p = e.Admission.Policy!;
        e.Admission.Policy = change switch
        {
            "checkpoint" => p with { RecoveryContinuation = p.RecoveryContinuation! with { PreviousJobSha256 = new string('d', 64) } },
            "deadline" => p with { ExpiresAtUtc = p.ExpiresAtUtc.AddSeconds(1) },
            _ => p with { SentenceRecovery = p.SentenceRecovery! with { GrantId = Guid.NewGuid() } }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => new ChirpArticleService(f, e.Admission, f.Paths, e.Clock)
            .PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.Equal(stopped, await e.Service.ReadWholeArticleJobAsync(stopped.JobId, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_split_attempts"));
    }

    private static async Task<(ChirpEvidence Evidence, ChirpWholeArticleJob Job, ChirpArticleRecoveryChild Failed,
        ChirpArticleAdmissionPolicy Policy)> StoppedRecoveryCheckpointAsync(Fixture f, bool compactSentenceSource = false)
    {
        var token = TestContext.Current.CancellationToken;
        var e = await AutomaticRecoveryEvidenceAsync(f, compactSentenceSource).ConfigureAwait(false);
        var p = e.Admission.Policy!;
        e.Admission.Policy = p with
        {
            MaximumNewAttempts = p.MaximumNewAttempts + 10,
            MaximumAdditionalReservationMinorUnits = p.MaximumAdditionalReservationMinorUnits + 10,
            Budget = p.Budget with
            {
                ProjectCeilingMinorUnits = p.Budget.ProjectCeilingMinorUnits + 10,
                AllocationCeilingMinorUnits = p.Budget.AllocationCeilingMinorUnits + 10
            }
        };
        var old = e.Admission.Policy!;
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, token).ConfigureAwait(false);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), token).ConfigureAwait(false);
        job = await CheckpointRecoveryJobAsync(e.Service, job, ChirpWholeArticleState.Executing, 1, "article.request-starting", token).ConfigureAwait(false);
        var runtime = new RecoveryFakeRuntime(f, e) { FailureMode = "both-depth1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GenerateAsync(job, 1, token)).ConfigureAwait(false);
        var root = Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts").ConfigureAwait(false));
        var children = await e.Service.PrepareRecoveryChildrenAsync(job.JobId, Guid.ParseExact((string)root[0]!, "N"), token).ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GenerateRecoveryAsync(job, children[0], token)).ConfigureAwait(false);
        job = await CheckpointRecoveryJobAsync(e.Service, job, ChirpWholeArticleState.Stopped, 1, "article.guard-rejected-reconcile", token).ConfigureAwait(false);
        return (e, job, children[0], old);
    }

    private static async Task<ChirpWholeArticleJob> CheckpointRecoveryJobAsync(ChirpArticleService service,
        ChirpWholeArticleJob job, ChirpWholeArticleState state, int ordinal, string reason, CancellationToken token)
    {
        var method = typeof(ChirpArticleService).GetMethod("TransitionWholeArticleAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return await ((Task<ChirpWholeArticleJob>)method.Invoke(service, [job, state, ordinal, reason, token, null, false])!).ConfigureAwait(false);
    }

    private static async Task AdmitRecoveryContinuationAsync(Fixture f, ChirpEvidence e, ChirpWholeArticleJob job,
        ChirpArticleRecoveryChild failed, ChirpArticleAdmissionPolicy old)
    {
        var roots = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false);
        var rows = (await FinancialRowsAsync(f, "chirp_article_successor_attempts").ConfigureAwait(false))
            .Concat(await FinancialRowsAsync(f, "chirp_article_split_attempts").ConfigureAwait(false)).ToArray();
        e.Admission.Policy = old with
        {
            AppSha256 = new string('b', 64),
            SourceApprovalSha256 = new string('e', 64),
            RecoveryContinuation = new(1, job.JobId, JsonHash(job), failed.AuthorizationId, old),
            PriorArticleAttemptRows = roots,
            PriorSuccessorAttemptRows = rows.Select(x => new RecoveryFinancialRowPin(JsonHash(x.Take(1).ToArray()), JsonHash(x))).ToArray(),
            MaximumNewAttempts = old.MaximumNewAttempts - 2,
            MaximumAdditionalReservationMinorUnits = old.MaximumAdditionalReservationMinorUnits - 2,
            Budget = old.Budget with
            {
                ProjectCommittedMinorUnits = old.Budget.ProjectCommittedMinorUnits + 2,
                AllocationCommittedMinorUnits = old.Budget.AllocationCommittedMinorUnits + 2,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            }
        };
    }
}
