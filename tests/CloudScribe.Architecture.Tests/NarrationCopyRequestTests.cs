#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Architecture.Tests;

public sealed class NarrationCopyRequestTests
{
    [Fact]
    public async Task GenuineCopySelectionUsesFilteredTextAndDistinctRequestIdentityAndCost()
    {
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        const string voice = "en-US-Standard-C";
        string original = "Read this. https://example.org/" + new string('x', 2500);
        string narration = NarrationUrlOmission.Transform(original).OutputText;
        var selection = new ShellViewModel.GoogleTtsStudioRequestSelection(Guid.NewGuid(), Guid.NewGuid(), original,
            "google", "account", "project", voice, "catalog", "voice-fingerprint", "en-US", "en-US", now);
        var capability = new GoogleCapabilitySnapshot("account", "capability", now.AddMinutes(-1), now.AddHours(1),
            new HashSet<string>([voice], StringComparer.Ordinal), new HashSet<string>(["MP3"], StringComparer.Ordinal), 5_000);
        var evidence = new GoogleGenerationProjectAuthorizationEvidence("account", "project", voice, "credential-reference", "capability", "endpoint", "global",
            "https://texttospeech.googleapis.com", true, now.AddMinutes(-1), now.AddHours(1));
        var factory = new GoogleTtsStudioRequestIntentFactory(new Authorization(evidence), new Clock(now));
        var before = await factory.CreateAsync(selection, capability, TestContext.Current.CancellationToken);
        var copy = selection with { DocumentId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), ExactText = narration };
        var after = await factory.CreateAsync(copy, capability, TestContext.Current.CancellationToken);
        var originalRequest = GoogleSpeechPlanCompiler.Compile(before.Plan, before.CompilationOptions);
        var narrationRequest = GoogleSpeechPlanCompiler.Compile(after.Plan, after.CompilationOptions);
        using var wire = JsonDocument.Parse(narrationRequest.Payload);
        Assert.Equal(narration, wire.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.NotEqual(before.IdempotencyKey, after.IdempotencyKey, StringComparer.Ordinal);
        Assert.NotEqual(before.RequestRevision, after.RequestRevision);
        Assert.Equal(original, selection.ExactText);
        var pricing = new V222ControlSet(new StrictJsonObjectReader());
        var originalCost = GoogleTtsStudioCostEstimator.Estimate(pricing, voice, originalRequest.Payload, "global");
        var copyCost = GoogleTtsStudioCostEstimator.Estimate(pricing, voice, narrationRequest.Payload, "global");
        Assert.True(copyCost.MaximumMinorUnits < originalCost.MaximumMinorUnits);
        Assert.True(copyCost.InputUtf8Bytes < originalCost.InputUtf8Bytes);
        // Unchecked legacy text continues to compile exactly, including its URL.
        using var oldWire = JsonDocument.Parse(originalRequest.Payload);
        Assert.Equal(original, oldWire.RootElement.GetProperty("input").GetProperty("text").GetString());
    }

    internal static async Task<GoogleGenerationProductionRequestIntent> IntentForAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection)
    {
        var now = DateTimeOffset.UtcNow;
        var capability = new GoogleCapabilitySnapshot(selection.AccountStableId, "capability", now.AddMinutes(-1), now.AddHours(1),
            new HashSet<string>([selection.VoiceStableId], StringComparer.Ordinal), new HashSet<string>(["MP3"], StringComparer.Ordinal), 5_000);
        var evidence = new GoogleGenerationProjectAuthorizationEvidence(selection.AccountStableId, selection.ProjectStableId, selection.VoiceStableId,
            "credential-reference", "capability", "endpoint", "global", "https://texttospeech.googleapis.com", true, now.AddMinutes(-1), now.AddHours(1));
        return await new GoogleTtsStudioRequestIntentFactory(new Authorization(evidence), new Clock(now)).CreateAsync(selection, capability, TestContext.Current.CancellationToken);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class Authorization(GoogleGenerationProjectAuthorizationEvidence value) : IGoogleGenerationProjectAuthorizationStore
    {
        public Task<GoogleGenerationProjectAuthorizationEvidence?> LoadCurrentAsync(string accountId, string projectId, string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoogleGenerationProjectAuthorizationEvidence?>(value);
        public Task<GoogleGenerationProjectAuthorizationEvidence?> LoadSingleCurrentForProjectAsync(string accountId, string projectId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoogleGenerationProjectAuthorizationEvidence?>(value);
        public Task SaveVerifiedAsync(GoogleGenerationProjectAuthorizationEvidence evidence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
