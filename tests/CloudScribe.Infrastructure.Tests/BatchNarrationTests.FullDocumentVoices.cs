using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Fact]
    public async Task Neural2BatchUsesFullSourceBytePlanAndNeverTreatsOfflineEstimateAsAdmission()
    {
        using var w = new Workspace();
        var library = new MemoryLibrary();
        var document = await library.CreateAsync("Full Neural2 comparison", GoogleFullDocumentPlanCompilerTests.ComparisonText(), Token);
        var settings = Settings with { LanguageCode = "en-US", VoiceStableId = "en-US-Neural2-D" };
        var batch = await w.CreateAsync(["comparison.txt"], settings);
        batch = batch with { Items = [batch.Items[0] with { DocumentId = document.Id, RevisionId = document.CurrentRevisionId }] };
        var request = await new BatchAdmissionRequestBuilder(library, new FixedClock(Now))
            .BuildAsync(batch, Profile(w.Root), Account, Project, cancellationToken: Token);
        var item = Assert.Single(request.Items);
        Assert.NotNull(item.Plan);
        Assert.Equal(ChirpArticlePlanCompiler.Neural2Version, item.Plan.PlannerVersion);
        Assert.Equal(8_166, item.Plan.SourceScalars);
        Assert.Equal(document.Text, string.Concat(item.Plan.Parts.Select(part => ChirpNarrationPlanner.Slice(document.Text, part.Range))));
        Assert.All(item.Plan.Parts, part => Assert.InRange(part.PayloadBytes, 1, 5_000));
        Assert.True(item.NeedsArticleAdmission);
        Assert.False(item.ExactMaximum);
        Assert.Contains("nothing is sent until then", item.Note!, StringComparison.Ordinal);
        Assert.Equal(ChirpArticlePlanCompiler.Compile(document, settings.LanguageCode, settings.VoiceStableId).PlanSha256, item.PlanSha256);
        Assert.Null(Assert.Single(batch.Items).JobId);
    }

    [Fact]
    public async Task ChirpBatchKeepsItsHistoricalSentencePlanIdentity()
    {
        using var w = new Workspace();
        var library = new MemoryLibrary();
        var document = await library.CreateAsync("Existing Chirp batch", "One sentence! Another sentence?", Token);
        var batch = await w.CreateAsync(["chirp.txt"]);
        batch = batch with { Items = [batch.Items[0] with { DocumentId = document.Id, RevisionId = document.CurrentRevisionId }] };
        var request = await new BatchAdmissionRequestBuilder(library, new FixedClock(Now))
            .BuildAsync(batch, Profile(w.Root), Account, Project, cancellationToken: Token);
        var item = Assert.Single(request.Items);
        Assert.NotNull(item.Plan);
        Assert.Equal(ChirpSentencePlanner.Version, item.Plan.PlannerVersion);
        Assert.Equal(ChirpArticlePlanCompiler.CompileSentences(document, Settings.LanguageCode, Settings.VoiceStableId).PlanSha256, item.PlanSha256);
    }
}
