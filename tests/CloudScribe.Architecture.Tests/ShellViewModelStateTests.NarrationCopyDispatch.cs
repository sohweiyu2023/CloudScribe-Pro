using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedCopyOrdinaryShellDispatchCapturesExactTextAndBlocksSourceEditAfterApproval(bool editSource)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        var original = await library.CreateAsync("Original", "Read https://example.org/private then continue.", TestContext.Current.CancellationToken);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(library, autosave);
        model.ConfigureNarrationCopies(CopyStore(fixture));
        await model.OpenDocumentCommand.ExecuteAsync(original.Id);
        var copy = await model.UseNarrationCopyAsync(original.Text);
        model.SelectedVoiceLabVoice = VoiceSelection() with { VoiceStableId = "en-US-Standard-C", LanguageCodes = ["en-US"] };
        var selection = model.CaptureGoogleTtsStudioRequestSelection();
        var intent = await NarrationCopyRequestTests.IntentForAsync(selection);
        var before = await NarrationCopyRequestTests.IntentForAsync(selection with { DocumentId = original.Id, RevisionId = original.CurrentRevisionId, ExactText = original.Text });
        Assert.NotEqual(before.IdempotencyKey, intent.IdempotencyKey, StringComparer.Ordinal);
        Assert.NotEqual(before.RequestRevision, intent.RequestRevision);
        var compiled = GoogleSpeechPlanCompiler.Compile(intent.Plan, intent.CompilationOptions);
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            selection.AccountStableId, intent.IdempotencyKey, compiled.Payload, "MP3");
        List<GenerationProviderRequest> sent = [];
        ConfigureCopyDispatch(model, selection, request, sent.Add);
        await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await model.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);
        if (editSource) await library.SaveAsync(new(original.Id, original.Title, "Edited original.", original.ConcurrencyVersion,
            DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken);
        await model.GenerateWithGoogleCommand.ExecuteAsync(null);
        if (editSource) Assert.Empty(sent);
        else
        {
            var actual = Assert.Single(sent);
            using var wire = JsonDocument.Parse(actual.CompiledPayload);
            Assert.Equal(copy.Narration.Text, wire.RootElement.GetProperty("input").GetProperty("text").GetString());
            Assert.Equal(intent.IdempotencyKey, actual.IdempotencyKey);
            Assert.Equal(copy.Narration.Id, selection.DocumentId); Assert.Equal(copy.Narration.CurrentRevisionId, selection.RevisionId);
            Assert.Equal(original.Text, (await library.OpenAsync(original.Id, TestContext.Current.CancellationToken))!.Text);
        }
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
    }

    [Theory]
    [InlineData("recovery", false)]
    [InlineData("chirp", false)]
    [InlineData("sample", false)]
    [InlineData("recovery", true)]
    [InlineData("sample", true)]
    public async Task StaleCopyDoesNotBlockSeparatelyAuthorizedRecoveryOrSampleButMismatchedPayloadDoes(string route, bool mismatch)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        var original = await library.CreateAsync("Original", "Read https://example.org then continue.", TestContext.Current.CancellationToken);
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel(); model.ConfigureStage3DocumentWorkflow(library, autosave);
        model.ConfigureNarrationCopies(CopyStore(fixture));
        await model.OpenDocumentCommand.ExecuteAsync(original.Id); await model.UseNarrationCopyAsync(original.Text);
        model.SelectedVoiceLabVoice = VoiceSelection() with { VoiceStableId = "en-US-Standard-C", LanguageCodes = ["en-US"] };
        var selection = model.CaptureGoogleTtsStudioRequestSelection();
        bool sample = string.Equals(route, "sample", StringComparison.Ordinal);
        var actualSelection = sample ? selection with { DocumentId = Guid.NewGuid(), RevisionId = null, ExactText = "Sample words." } : selection;
        var intent = await NarrationCopyRequestTests.IntentForAsync(actualSelection);
        var compiled = GoogleSpeechPlanCompiler.Compile(intent.Plan, intent.CompilationOptions);
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            selection.AccountStableId, intent.IdempotencyKey, compiled.Payload, "MP3");
        var envelope = new GoogleGenerationSubmissionEnvelope(selection.AccountStableId, "credential", "capability", "pricing-test", 1,
            selection.VoiceStableId, "MP3", Convert.ToHexStringLower(SHA256.HashData(compiled.Payload.Span)), compiled.Payload.Length,
            RecoveryAuthorizationId: sample || string.Equals(route, "chirp", StringComparison.Ordinal) ? null : Guid.NewGuid(),
            ChirpAuthorizationId: string.Equals(route, "chirp", StringComparison.Ordinal) ? Guid.NewGuid() : null);
        List<GenerationProviderRequest> sent = [];
        ConfigureCopyDispatch(model, selection, request, sent.Add, sample ? null : mismatch ? envelope with { CompiledPayloadSha256 = new string('f', 64) } : envelope,
            mismatch && sample);
        await PrepareCopySeparateRouteAsync(model, actualSelection, route, envelope, compiled.Payload);
        await library.SaveAsync(new(original.Id, original.Title, "Changed original after independent approval.", original.ConcurrencyVersion,
            DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken);
        await model.GenerateWithGoogleCommand.ExecuteAsync(null);
        Assert.Equal(mismatch ? 0 : 1, sent.Count);
    }

    private static async Task PrepareCopySeparateRouteAsync(ShellViewModel model, ShellViewModel.GoogleTtsStudioRequestSelection selection,
        string route, GoogleGenerationSubmissionEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        if (string.Equals(route, "sample", StringComparison.Ordinal))
        {
            await model.PrepareGoogleVoiceSampleAsync(selection.ExactText, selection.DocumentId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await model.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(route, "chirp", StringComparison.Ordinal))
        {
            var review = new ChirpRequestReview(Guid.NewGuid(), new string('b', 64), "plan", 1, selection.DocumentId, selection.RevisionId!.Value,
                envelope.CompiledPayloadSha256, payload.Length, "USD", 2, 1, 3, 3, 100, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1),
                "pricing-test", selection.VoiceStableId, IsCatalogRevalidation: true);
            model.ConfigureChirpArticle((_, _, _) => Task.FromResult(review), (_, _, _) => Task.CompletedTask, (_, _, _) => Task.FromResult("unused.mp3"));
            await model.PrepareChirpArticleAsync(1, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await model.ApproveChirpArticleAsync(new(review.PreviewId, review.ReviewSha256, 3, true, true), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            var old = RecoveryPreviewForUi(selection);
            var review = old with { Intent = old.Intent with { Envelope = envelope, CompiledPayload = payload } };
            model.ConfigureGoogleRecovery((_, _, _, _) => Task.FromResult(review), (_, _, _) => Task.CompletedTask);
            await model.PrepareGoogleRecoveryAsync("held", 3, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await model.ApproveGoogleRecoveryAsync(new(review.PreviewId, review.ReviewSha256, 3, true, true), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    private static void ConfigureCopyDispatch(ShellViewModel model, ShellViewModel.GoogleTtsStudioRequestSelection selection,
        GenerationProviderRequest request, Action<GenerationProviderRequest> observe, GoogleGenerationSubmissionEnvelope? envelope = null, bool wrongReviewHash = false)
    {
        var clean = new GoogleGenerationPersistedQueueState(request.AccountId, request.OperationStableId, request.IdempotencyKey, false, null);
        var stored = clean;
        var queue = new GoogleGenerationQueueCoordinator((actual, _, _) =>
        {
            observe(actual);
            return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.RejectedSafeToRetry, null,
                ReadOnlyMemory<byte>.Empty, null, null, "google-http-400", GoogleErrorStatus.InvalidArgument));
        });
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, (_, _, _, _) => Task.FromResult<GoogleGenerationPersistedQueueState?>(stored),
            (value, _) => { stored = value; return Task.CompletedTask; }, (value, _) => { stored = value; return Task.FromResult(true); },
            (value, _) => { stored = value; return Task.CompletedTask; });
        var trust = LoggingTrust() with
        {
            AccountId = selection.AccountStableId,
            ProjectId = selection.ProjectStableId,
            VoiceStableId = selection.VoiceStableId,
            ResolvedModelId = selection.VoiceStableId,
            CapabilityIdentity = "capability"
        };
        var snapshot = new GoogleGenerationUiExecutionSnapshot(new(request.AccountId, selection.ProjectStableId, selection.VoiceStableId,
            selection.VoiceStableId, "capability", "MP3"), true, true, true, true, request, trust, clean, clean,
            GoogleGenerationReconciliationResolutionEvidence.None, true, true, true, true);
        model.ConfigureStage6GoogleGeneration(_ => Task.FromResult(new GoogleGenerationUiExecutionContext(new(bound), snapshot, envelope)));
        model.ConfigureStage6GoogleGenerationPreparation(_ => Task.FromResult(new ShellViewModel.GoogleGenerationSpendReview("USD", 2, 50,
            "pricing-test", selection.VoiceStableId, wrongReviewHash ? new string('f', 64) : Convert.ToHexStringLower(SHA256.HashData(request.CompiledPayload.Span)))));
        model.ConfigureStage6GoogleGenerationSpendApproval((_, _, _) => Task.CompletedTask);
    }
}
