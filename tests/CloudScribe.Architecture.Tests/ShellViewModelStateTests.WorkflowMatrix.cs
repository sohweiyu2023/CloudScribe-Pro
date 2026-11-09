using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    public static TheoryData<string> WorkflowMatrixData => new(WorkflowMatrixCases.ReadIds());

    [Theory]
    [MemberData(nameof(WorkflowMatrixData))]
    public async Task WorkflowMatrixRetainsExactTextEvidenceAndDurableBarriers(string caseId)
    {
        string[] row = caseId.Split('|');
        Assert.True(WorkflowMatrixCases.IsValid(row));
        TestContext.Current.TestOutputHelper!.WriteLine("seed={0}; case={1}", WorkflowMatrixCases.Seed, caseId);
        var token = TestContext.Current.CancellationToken;
        await using var f = await CatalogReopenFixture.CreateAsync();
        await f.InitializeNativeCompilerAsync();
        int vaultWrites = f.Vault.Writes;
        var source = await f.Disk.NewLibrary().CreateAsync("Synthetic matrix original", MatrixSource(row[2]), token);
        string sourceFile = Assert.Single(Directory.GetFiles(f.Disk.Paths.DocumentsDirectory, "*.utf8", SearchOption.AllDirectories));
        byte[] sourceBytes = await File.ReadAllBytesAsync(sourceFile, token);
        var narration = await PrepareMatrixNarrationAsync(f, source, row[1]);
        string expectedText = MatrixExpectedText(row[2], row[1]);
        Assert.Equal(expectedText, narration.Text);

        using var initial = f.NewModel();
        var pipeline = new MatrixPipeline(f);
        var state = await PrepareMatrixStateAsync(f, initial, pipeline, narration, row);
        await VerifyMatrixLifecycleAsync(f, initial, pipeline, state, row, narration);
        Assert.Equal(narration, await PrepareMatrixNarrationAsync(f, source, row[1]));
        Assert.Equal(source, await f.Disk.NewLibrary().OpenAsync(source.Id, token));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourceFile, token));
        Assert.Equal(vaultWrites, f.Vault.Writes);
        Assert.Equal(0, f.Handler.PaidRequests);
    }

    private sealed record MatrixReviewState(GoogleGenerationProductionPendingApprovalStateOwner.PendingState Before,
        ShellViewModel.GoogleGenerationSpendReview Review, GoogleGenerationPersistedQueueState Persisted,
        GoogleGenerationSpendAuthorization? Authorization, Guid CapabilityId, DateTimeOffset ExpiresAtUtc);

    private static async Task<MatrixReviewState> PrepareMatrixStateAsync(CatalogReopenFixture f, ShellViewModel initial,
        MatrixPipeline pipeline, DocumentSnapshot narration, string[] row)
    {
        var token = TestContext.Current.CancellationToken;
        await f.OpenAsync(initial).ConfigureAwait(false);
        await f.SelectionAsync(initial, narration, MatrixVoice(row[0])).ConfigureAwait(false);
        pipeline.Bind(initial);
        var review = await initial.PrepareGoogleGenerationSpendReviewAsync(token).ConfigureAwait(false);
        var before = (await pipeline.Pending.ResolveCurrentAsync(token).ConfigureAwait(false))!;
        Assert.NotNull(before);
        AssertMatrixPayload(before, MatrixExpectedText(row[2], row[1]), MatrixVoice(row[0]));
        Assert.Equal(before.Envelope.CompiledPayloadSha256, review.CompiledPayloadSha256);
        Assert.Equal(before.CurrentEstimateMinorUnits, review.CurrentEstimateMinorUnits);
        Assert.False(before.Snapshot.PricingApproved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => initial.ApproveGoogleGenerationSpendAsync(review.CurrentEstimateMinorUnits, false, token)).ConfigureAwait(false);
        Assert.Null(await pipeline.Spend.LoadApprovedAsync(before.Envelope, token).ConfigureAwait(false));
        Assert.Null(await pipeline.Submission.ResolveCurrentAsync(token).ConfigureAwait(false));

        bool approved = !string.Equals(row[6], "pending", StringComparison.Ordinal);
        if (approved)
        {
            await initial.ApproveGoogleGenerationSpendAsync(review.CurrentEstimateMinorUnits, true, token).ConfigureAwait(false);
            Assert.Equal(before.Envelope, (await pipeline.Runtime.ResolveAsync(token).ConfigureAwait(false)).SubmissionEnvelope);
        }
        var persisted = await MatrixPersistOutcomeAsync(pipeline, before, row[6], f.Clock.GetUtcNow()).ConfigureAwait(false);
        var authorizationBefore = await pipeline.Spend.LoadApprovedAsync(before.Envelope, token).ConfigureAwait(false);
        var capabilityBefore = (await f.Capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId, CatalogReopenFixture.Account, token).ConfigureAwait(false))!;
        return new(before, review, persisted, authorizationBefore, capabilityBefore.Id, capabilityBefore.ExpiresAtUtc);
    }

    private static async Task VerifyMatrixLifecycleAsync(CatalogReopenFixture f, ShellViewModel initial, MatrixPipeline pipeline,
        MatrixReviewState state, string[] row, DocumentSnapshot narration)
    {
        var token = TestContext.Current.CancellationToken;
        bool recreated = !string.Equals(row[5], "same-session", StringComparison.Ordinal);
        bool chinese = string.Equals(row[5], "reopen-chinese", StringComparison.Ordinal);
        if (recreated)
        {
            new StudioPreferencesStore(f.Disk.Paths).Remember(new VoiceSetup("Persisted matrix setup", chinese ? "cmn-CN" : "en-US",
                chinese ? "cmn-CN-Chirp3-HD-Alnilam" : MatrixVoice(row[0])));
            initial.Dispose();
        }
        using var reopened = recreated ? f.NewModel() : null;
        var model = reopened ?? initial;
        if (recreated)
        {
            pipeline = new MatrixPipeline(f); pipeline.Bind(model);
            model.VoiceLabSearchText = "stale search that excludes the saved voice";
            model.ConfigureSavedVoiceSetups(new StudioPreferencesStore(f.Disk.Paths));
            Assert.Null(model.VoiceLabSearchText);
            Assert.Equal(chinese ? "cmn-CN" : "en-US", model.VoiceLabLocaleFilter);
        }
        await MutateMatrixEvidenceAsync(f, row[4]).ConfigureAwait(false);
        if (string.Equals(row[3], "refresh", StringComparison.Ordinal)) Assert.True(await f.Loader.RefreshAsync(model, token).ConfigureAwait(false), model.ConnectedVoiceCatalogStatus);
        else if (string.Equals(row[3], "filter", StringComparison.Ordinal)) Assert.True(await f.Loader.ApplyFiltersAsync(model, token).ConfigureAwait(false), model.ConnectedVoiceCatalogStatus);
        else await f.OpenAsync(model).ConfigureAwait(false);
        Assert.True(model.HasCurrentVoiceLanguageCatalog, model.ConnectedVoiceCatalogStatus);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.Null(await pipeline.Pending.ResolveCurrentAsync(token).ConfigureAwait(false));
        Assert.Null(await pipeline.Submission.ResolveCurrentAsync(token).ConfigureAwait(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.Runtime.ResolveAsync(token)).ConfigureAwait(false);
        await model.GenerateWithGoogleCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.Equal(0, pipeline.CanarySubmissions);
        Assert.Equal(0, pipeline.OutputPublicationAttempts);
        Assert.Equal(0, pipeline.PlaybackAttempts);

        var capabilityAfter = (await f.Capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId, CatalogReopenFixture.Account, token).ConfigureAwait(false))!;
        bool changed = !string.Equals(row[4], "unchanged", StringComparison.Ordinal) || string.Equals(row[3], "refresh", StringComparison.Ordinal);
        if (changed) Assert.NotEqual(state.CapabilityId, capabilityAfter.Id);
        else
        {
            Assert.Equal(state.CapabilityId, capabilityAfter.Id);
            Assert.Equal(state.ExpiresAtUtc, capabilityAfter.ExpiresAtUtc);
        }
        await VerifyMatrixRecompileAsync(f, model, pipeline, state, row, narration, changed).ConfigureAwait(false);
    }

    private static async Task VerifyMatrixRecompileAsync(CatalogReopenFixture f, ShellViewModel model, MatrixPipeline pipeline,
        MatrixReviewState state, string[] row, DocumentSnapshot narration, bool changed)
    {
        var token = TestContext.Current.CancellationToken;
        var before = state.Before;
        var persisted = state.Persisted;
        await f.SelectionAsync(model, narration, MatrixVoice(row[0])).ConfigureAwait(false);
        var preview = await f.NativeCompiler!.CompileReadonlyAsync(model.CaptureGoogleTtsStudioRequestSelection(), token).ConfigureAwait(false);
        AssertMatrixPayload(preview, MatrixExpectedText(row[2], row[1]), MatrixVoice(row[0]));
        Assert.Equal(before.Envelope.CompiledPayloadSha256, preview.Envelope.CompiledPayloadSha256);
        Assert.Equal(before.CurrentEstimateMinorUnits, preview.CurrentEstimateMinorUnits);
        if (changed)
        {
            Assert.NotEqual(before.Envelope.RequestRevision, preview.Envelope.RequestRevision);
            Assert.NotEqual(before.Snapshot.ProviderRequest.IdempotencyKey, preview.Snapshot.ProviderRequest.IdempotencyKey, StringComparer.Ordinal);
            Assert.Null(await pipeline.Spend.LoadApprovedAsync(preview.Envelope, token).ConfigureAwait(false));
        }
        else Assert.Equal(before.Envelope, preview.Envelope);

        if (persisted.SubmissionAttempted && !changed)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => model.PrepareGoogleGenerationSpendReviewAsync(token)).ConfigureAwait(false);
            Assert.Contains("prior submission", error.Message, StringComparison.Ordinal);
            Assert.Null(await pipeline.Pending.ResolveCurrentAsync(token).ConfigureAwait(false));
        }
        else
        {
            var after = await model.PrepareGoogleGenerationSpendReviewAsync(token).ConfigureAwait(false);
            Assert.Equal(state.Review, after);
            Assert.False(model.PreparedGoogleGenerationSpendApproved);
            Assert.Null(await pipeline.Submission.ResolveCurrentAsync(token).ConfigureAwait(false));
        }
        Assert.Equal(state.Authorization, await new GoogleGenerationSpendAuthorizationStore(f.Factory, f.Clock).LoadApprovedAsync(before.Envelope, token).ConfigureAwait(false));
        var loaded = await new GoogleGenerationPersistedQueueStateStore(f.Factory).LoadAsync(persisted.AccountId, persisted.OperationStableId, persisted.IdempotencyKey, token).ConfigureAwait(false);
        Assert.Equal(persisted, loaded);
        if (persisted.SubmissionAttempted)
        {
            Assert.False(await pipeline.Queue.TryClaimSubmissionAsync(MatrixClaim(before, f.Clock.GetUtcNow()), token).ConfigureAwait(false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.Queue.SaveAsync(before.Snapshot.CurrentState, token)).ConfigureAwait(false);
        }
        Assert.False(model.CanGenerateWithGoogle);
        await model.GenerateWithGoogleCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.Equal(0, pipeline.CanarySubmissions);
        Assert.Equal(0, pipeline.OutputPublicationAttempts);
        Assert.Equal(0, pipeline.PlaybackAttempts);
    }

    private static string MatrixVoice(string key) => key switch
    {
        "neural2" => "en-US-Neural2-D",
        "aoede" => "en-US-Chirp3-HD-Aoede",
        _ => throw new InvalidDataException("Unknown matrix voice."),
    };

    private static string MatrixSource(string key) => key switch
    {
        "plain" => "Read this synthetic passage.",
        "explicit-url" => "Read https://example.test/page then finish.",
        "bare-domain" => "Read 84000.co then finish.",
        "unicode" => "\u4e2d\u6587 Read https://example.test/page and 84000.co then finish \U0001f642.",
        "url-only" => "https://example.test/page",
        _ => throw new InvalidDataException("Unknown matrix text."),
    };

    private static string MatrixExpectedText(string text, string omission) => (text, omission) switch
    {
        (_, "off") or ("plain", _) or ("bare-domain", "v1") => MatrixSource(text),
        ("explicit-url", _) or ("bare-domain", "v2") => "Read  then finish.",
        ("unicode", "v1") => "\u4e2d\u6587 Read  and 84000.co then finish \U0001f642.",
        ("unicode", "v2") => "\u4e2d\u6587 Read  and  then finish \U0001f642.",
        _ => throw new InvalidDataException("Invalid matrix text policy."),
    };

    private static async Task<DocumentSnapshot> PrepareMatrixNarrationAsync(CatalogReopenFixture f, DocumentSnapshot source, string policy)
    {
        if (string.Equals(policy, "off", StringComparison.Ordinal)) return source;
        var store = new EfNarrationCopyStore(f.Factory, new(f.Disk.Paths), f.Clock);
        var prepared = await store.PrepareAsync(source.Id, source.CurrentRevisionId!.Value,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Text))),
            // The frozen pairwise inventory exercises the saved v1/v2 policies that existing copies replay; the current
            // v3 policy is covered by its own unit and copy-store tests.
            string.Equals(policy, "v1", StringComparison.Ordinal) ? NarrationUrlOmission.LegacyVersion : NarrationUrlOmission.BareDomainV2Version,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal("url-omission-" + policy, prepared.TransformVersion);
        return prepared.Narration;
    }

    private static void AssertMatrixPayload(GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending, string expected, string voice)
    {
        pending.Validate();
        using var payload = JsonDocument.Parse(pending.Snapshot.ProviderRequest.CompiledPayload);
        Assert.Equal(expected, payload.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.Equal(voice, payload.RootElement.GetProperty("voice").GetProperty("name").GetString());
        Assert.Equal(voice, pending.Envelope.VoiceName);
        Assert.Equal(CatalogReopenFixture.Account, pending.Envelope.AccountId);
        Assert.Equal(CatalogReopenFixture.Project, pending.Snapshot.UiSelection.ProjectId);
    }

    private static async Task MutateMatrixEvidenceAsync(CatalogReopenFixture f, string evidence)
    {
        if (string.Equals(evidence, "missing", StringComparison.Ordinal))
        {
            using var db = f.Factory.CreateDbContext();
            Assert.True(await db.Database.ExecuteSqlRawAsync("DELETE FROM google_generation_project_authorizations", TestContext.Current.CancellationToken).ConfigureAwait(false) > 0);
        }
        else await f.ApplyReopenMutationAsync(string.Equals(evidence, "metadata", StringComparison.Ordinal) ? "voice-metadata" : evidence).ConfigureAwait(false);
    }

    private static GoogleGenerationPersistedQueueState MatrixClaim(GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending, DateTimeOffset now) =>
        pending.Snapshot.CurrentState with
        {
            SubmissionAttempted = true,
            UnresolvedSubmission = true,
            LocalAttemptId = Guid.NewGuid().ToString("N"),
            DiagnosticCode = "google-dispatch-claimed",
            PayloadSha256 = pending.Envelope.CompiledPayloadSha256,
            PayloadBytes = pending.Envelope.CompiledPayloadBytes,
            OutcomeObservedAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
        };

    private static async Task<GoogleGenerationPersistedQueueState> MatrixPersistOutcomeAsync(MatrixPipeline pipeline,
        GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending, string work, DateTimeOffset now)
    {
        if (work is "pending" or "approved") return pending.Snapshot.CurrentState;
        // Synthetic persisted outcomes only. No provider adapter or physical submission is invoked.
        var claim = MatrixClaim(pending, now);
        Assert.True(await pipeline.Queue.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken).ConfigureAwait(false));
        var outcome = claim with
        {
            UnresolvedSubmission = string.Equals(work, "uncertain", StringComparison.Ordinal),
            DiagnosticCode = string.Equals(work, "uncertain", StringComparison.Ordinal) ? "google-transport-unknown" : "google-accepted"
        };
        await pipeline.Queue.SaveOutcomeAsync(outcome, TestContext.Current.CancellationToken).ConfigureAwait(false);
        return outcome;
    }
}
