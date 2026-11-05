using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task RevisedLimitsNeverRetryAnUncertainDeeperChild()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, failed, old) = await StoppedAtDepthTwoAsync(f);
        await AdmitStructuralRevisionAsync(f, e, stopped, failed, old);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var same = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new RecoveryFakeRuntime(f, e) { FailureMode = "child-uncertain" };
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Single(runtime.Children); Assert.Equal(3, runtime.Children[0].Depth); Assert.Equal(0, runtime.RootFailures);
        var blocked = await e.Service.ReadWholeArticleJobAsync(same.JobId, TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Stopped, blocked.State);
        Assert.Equal(blocked, await new ChirpArticleService(f, e.Admission, f.Paths, e.Clock)
            .RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Single(runtime.Children);
    }

    [Fact]
    public async Task UnchangedWholeReviewUsesActualPolicyDeadlineRatherThanTenMinuteTimer()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(e.Admission.Policy!.ExpiresAtUtc, review.ConfirmByUtc);
        e.Clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(ChirpWholeArticleState.Ready,
            (await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken)).State);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task RevisedLimitsPreserveOldDepthTwoFailureAndExportEightOrderedRealLeavesWithoutReplay()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, failed, old) = await StoppedAtDepthTwoAsync(f);
        var mappingBefore = JsonHash(await FinancialRowsAsync(f, "chirp_article_split_mappings"));
        await AdmitStructuralRevisionAsync(f, e, stopped, failed, old);
        TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_STRUCTURAL_RECOVERY_POLICY_BASE64="
            + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(e.Admission.Policy)));
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var same = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Equal(stopped.JobId, same.JobId); Assert.Equal(stopped.ExpiresAtUtc, same.ExpiresAtUtc);
        var runtime = new RecoveryFakeRuntime(f, e) { FailureMode = "below-depth3" };
        var done = await e.Service.RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, done.State); Assert.Equal(0, runtime.RootFailures);
        Assert.DoesNotContain(runtime.Children, x => x.AuthorizationId == failed.AuthorizationId);
        Assert.Contains(runtime.Children, x => x.Depth == 3);
        Assert.Equal(14, (await FinancialRowsAsync(f, "chirp_article_split_attempts")).Count);
        Assert.Equal(8, (await FinancialRowsAsync(f, "chirp_article_split_receipts")).Count);
        var mappings = await FinancialRowsAsync(f, "chirp_article_split_mappings");
        Assert.Equal(mappingBefore, JsonHash(mappings.Take(2).ToArray()));
        var native = new RecoveryExportNative();
        var exporter = new ArticleAudioExportService(f, f.Library, new GoogleGenerationPersistedQueueStateStore(f), f.Paths, native, chirpArticles: e.Service);
        string output = Path.Combine(f.Root, "deep-ordered-export.mp3");
        await exporter.ExportAsync(e.Document.Id, output, CreateArticleTools(f.Root), TestContext.Current.CancellationToken);
        Assert.True(native.Decoded); Assert.True(native.Inputs >= 8); Assert.True(File.Exists(output));
        int calls = runtime.Calls;
        Assert.Equal(done, await new ChirpArticleService(f, e.Admission, f.Paths, e.Clock)
            .RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(calls, runtime.Calls);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("predecessor")]
    [InlineData("ceiling")]
    [InlineData("depth")]
    public async Task StructuralLimitRevisionCannotResetGrantOrInventProjectProvision(string changed)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, job, failed, old) = await StoppedAtDepthTwoAsync(f);
        await AdmitStructuralRevisionAsync(f, e, job, failed, old);
        var p = e.Admission.Policy!;
        p = changed switch
        {
            "grant" => p with { SentenceRecovery = p.SentenceRecovery! with { GrantId = Guid.NewGuid() } },
            "predecessor" => p with { RecoveryLimitRevision = p.RecoveryLimitRevision! with { PreviousGrantSha256 = new string('f', 64) } },
            "ceiling" => p with
            {
                RecoveryLimitRevision = p.RecoveryLimitRevision! with { MaximumProjectMinorUnits = 918 },
                Budget = p.Budget with { ProjectCeilingMinorUnits = 918 }
            },
            _ => p with { SentenceRecovery = p.SentenceRecovery! with { MaximumDepth = 4 } }
        };
        Assert.Throws<InvalidDataException>(() => ChirpPolicyValidation.RequireValid(p, e.Clock.GetUtcNow()));
        Assert.Equal(job, await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FortyWholeArticleReviewsSupersedeOnlyStaleConfirmationWithoutSpending()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AutomaticRecoveryEvidenceAsync(f);
        var first = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var last = first;
        for (int i = 0; i < 40; i++) last = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.StartWholeArticleAsync(WholeConfirmation(first), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(ChirpWholeArticleState.Ready,
            (await e.Service.StartWholeArticleAsync(WholeConfirmation(last), TestContext.Current.CancellationToken)).State);
    }

    private static async Task<(ChirpEvidence Evidence, ChirpWholeArticleJob Job, ChirpArticleRecoveryChild Failed,
        ChirpArticleAdmissionPolicy Policy)> StoppedAtDepthTwoAsync(Fixture f)
    {
        var (e, stopped, first, old) = await StoppedRecoveryCheckpointAsync(f, compactSentenceSource: true).ConfigureAwait(false);
        var token = TestContext.Current.CancellationToken;
        var executing = await CheckpointRecoveryJobAsync(e.Service, stopped, ChirpWholeArticleState.Executing, 1, "article.request-starting", token).ConfigureAwait(false);
        var children = await e.Service.PrepareRecoveryChildrenAsync(stopped.JobId, first.AuthorizationId, token).ConfigureAwait(false);
        var runtime = new RecoveryFakeRuntime(f, e) { FailureMode = "all-too-long" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GenerateRecoveryAsync(executing, children[0], token)).ConfigureAwait(false);
        stopped = await CheckpointRecoveryJobAsync(e.Service, executing, ChirpWholeArticleState.Stopped, 1, "article.guard-rejected-reconcile", token).ConfigureAwait(false);
        return (e, stopped, children[0], old);
    }

    private static async Task AdmitStructuralRevisionAsync(Fixture f, ChirpEvidence e, ChirpWholeArticleJob job,
        ChirpArticleRecoveryChild failed, ChirpArticleAdmissionPolicy old)
    {
        await AdmitRecoveryContinuationAsync(f, e, job, failed, old).ConfigureAwait(false);
        var p = e.Admission.Policy!;
        long delta = 320 - old.SentenceRecovery!.MaximumAdditionalMinorUnits;
        long project = old.Budget.ProjectCeilingMinorUnits + delta;
        long committed = old.Budget.ProjectCommittedMinorUnits + 3;
        e.Admission.Policy = p with
        {
            MaximumNewAttempts = checked((int)(project - committed)),
            MaximumAdditionalReservationMinorUnits = project - committed,
            SentenceRecovery = old.SentenceRecovery with
            {
                MaximumDepth = old.Articles.SelectMany(x => x.Proposal.Parts)
                .Max(x => x.Range.EndScalarExclusive - x.Range.StartScalar) - 1,
                MaximumChildAttemptsPerRange = 320,
                MaximumChildAttemptsPerJob = 320,
                MaximumChildAttemptsTotal = 320,
                MaximumAdditionalMinorUnits = 320
            },
            RecoveryLimitRevision = new(1, JsonHash(old.SentenceRecovery), project),
            Budget = p.Budget with
            {
                ProjectCommittedMinorUnits = committed,
                AllocationCommittedMinorUnits = old.Budget.AllocationCommittedMinorUnits + 3,
                ProjectCeilingMinorUnits = project,
                AllocationCeilingMinorUnits = old.Budget.AllocationCeilingMinorUnits + delta
            }
        };
    }

    private sealed class RecoveryExportNative : INativeMediaTool
    {
        public int Inputs { get; private set; }
        public bool Decoded { get; private set; }
        public async Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            string result = string.Empty;
            if (invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal))
                result = JsonSerializer.Serialize(new
                {
                    streams = new[] { new { codec_name = "mp3", sample_rate = "24000", channels = 1 } },
                    format = new { duration = Path.GetFileName(invocation.Arguments[^1]).StartsWith("input-", StringComparison.Ordinal) ? "1" : Math.Max(Inputs, 1).ToString(CultureInfo.InvariantCulture) }
                });
            else if (invocation.Arguments.Contains("concat", StringComparer.Ordinal))
            {
                string manifest = await File.ReadAllTextAsync(Path.Combine(invocation.WorkingDirectory, "article-inputs.ffconcat"), cancellationToken).ConfigureAwait(false);
                Inputs = manifest.Split('\n').Count(x => x.StartsWith("file '", StringComparison.Ordinal));
                byte[] bytes = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(bytes, 0);
                await File.WriteAllBytesAsync(invocation.Arguments[^1], bytes, cancellationToken).ConfigureAwait(false);
            }
            else if (invocation.Arguments.Contains("null", StringComparer.Ordinal)) Decoded = true;
            return new(0, false, result, string.Empty, TimeSpan.Zero); // Synthetic media adapter; no real provider or media process.
        }
    }
}
