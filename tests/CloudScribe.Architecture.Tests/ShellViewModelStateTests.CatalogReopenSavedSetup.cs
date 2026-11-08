using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogReopenSavedSetupRetainsEvidenceWithoutImplicitDiagnosticClaim(bool diagnostic)
    {
        await using var f = await CatalogReopenFixture.CreateAsync();
        using var model = f.NewModel();
        await f.OpenAsync(model);
        var before = await f.IntentAsync(model, "en-US-Neural2-D");
        var capability = await f.Capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId,
            CatalogReopenFixture.Account, TestContext.Current.CancellationToken);
        if (diagnostic) f.DiagnosticConfiguration = SavedSetupDiagnosticConfiguration();
        int requests = f.Handler.TotalRequests;
        foreach (string language in new[] { "cmn-CN", "en-US", "en-US" })
        {
            string voice = string.Equals(language, "cmn-CN", StringComparison.Ordinal) ? "cmn-CN-Chirp3-HD-Alnilam" : "en-US-Neural2-D";
            model.VoiceLabSearchText = "previous filter that excludes the saved voice";
            model.ApplySavedVoiceSetup(new VoiceSetup("Synthetic saved setup", language, voice));
            Assert.Null(model.VoiceLabSearchText);
            bool applied = await f.Loader.ApplyFiltersAsync(model, TestContext.Current.CancellationToken);
            Assert.Equal(!diagnostic, applied);
            if (diagnostic)
            {
                Assert.Empty(model.VoiceLabCatalogResults);
                Assert.False(model.HasCurrentVoiceLanguageCatalog);
                Assert.Null(model.SelectedVoiceLabVoice);
                Assert.Contains("has not started verification or consumed a refresh", model.ConnectedVoiceCatalogStatus, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(model.HasCurrentVoiceLanguageCatalog);
                Assert.Equal(voice, model.SelectedVoiceLabVoice!.VoiceStableId);
                if (string.Equals(language, "en-US", StringComparison.Ordinal))
                {
                    var after = await f.IntentAsync(model, voice);
                    Assert.Equal(before.RequestRevision, after.RequestRevision);
                    Assert.Equal(before.IdempotencyKey, after.IdempotencyKey);
                }
            }
            var current = await f.Capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId,
                CatalogReopenFixture.Account, TestContext.Current.CancellationToken);
            Assert.Equal(capability!.Id, current!.Id);
            Assert.Equal(capability.ExpiresAtUtc, current.ExpiresAtUtc);
        }
        Assert.Equal(0, f.DiagnosticRunner.Commands);
        Assert.Equal(0, f.Handler.PaidRequests);
        if (diagnostic) Assert.Equal(requests, f.Handler.TotalRequests);
    }

    private static CompletionGateConfiguration SavedSetupDiagnosticConfiguration() => new(
        SchemaVersion: 1, PythonPath: "synthetic", PythonSha256: "synthetic", ScriptPath: "synthetic", ScriptSha256: "synthetic",
        ModuleSha256: "synthetic", RegisterPath: "synthetic", ScopeSha256: "synthetic", ApplicationDirectory: "synthetic",
        CandidateApplicationDirectory: "synthetic", AppSha256: "synthetic", InfrastructureSha256: "synthetic", ProfileRootSha256: "synthetic",
        AccountId: CatalogReopenFixture.Account, ProjectId: CatalogReopenFixture.Project, CredentialReferenceId: "synthetic-reopen-key",
        CatalogEndpoint: "https://texttospeech.googleapis.com/v1/voices", SynthesisEndpoint: "https://texttospeech.googleapis.com/v1/text:synthesize",
        PlanManifestPath: "synthetic", PlanManifestSha256: "synthetic", VoiceName: "en-US-Neural2-D", LanguageCode: "en-US");

    private sealed class CatalogReopenGateRunner : ICompletionGateCommandRunner
    {
        internal int Commands { get; private set; }
        public void Run(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt)
        {
            Commands++;
            throw new InvalidOperationException("Saved setup selection must not execute diagnostic commands.");
        }
    }
}
