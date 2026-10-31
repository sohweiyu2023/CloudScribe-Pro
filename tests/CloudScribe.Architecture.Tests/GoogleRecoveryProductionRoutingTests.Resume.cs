using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Fact]
    public async Task ActualCompositionRevalidatesSameHeldRequestWithRecapturedSelectionAndPrivateObservation()
    {
        using var f = new Fixture(); var r = await PrepareResumeFixtureAsync(f);
        Assert.False(r.Draft.GrantsAuthority);
        await GoogleTtsUsabilityHotfixBinder.SaveReservedResumeDraftAsync(f.ResumeDraftPath, r.Draft, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => GoogleTtsUsabilityHotfixBinder.SaveReservedResumeDraftAsync(f.ResumeDraftPath, r.Draft, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(f.Selection.ExactText, await File.ReadAllTextAsync(f.ResumeDraftPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: r.Service);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(r.Service, f.ChirpCompiler, owner, TimeProvider.System);
        var review = await preparation.PrepareAsync(f.Selection with { CapturedAtUtc = DateTimeOffset.UtcNow }, 1, TestContext.Current.CancellationToken);
        Assert.True(review.IsCatalogRevalidation);
        await preparation.ApproveAsync(f.Selection with { CapturedAtUtc = DateTimeOffset.UtcNow }, ChirpConfirm(review), TestContext.Current.CancellationToken);
        var active = (await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(r.Policy.ExecutionEnvelope, active.SubmissionEnvelope);
        var observed = await preparation.CompileReadonlyAsync(f.Selection with { CapturedAtUtc = DateTimeOffset.UtcNow }, TestContext.Current.CancellationToken);
        Assert.Equal(active.Snapshot.ProviderRequest.CompiledPayload.ToArray(), observed.Snapshot.ProviderRequest.CompiledPayload.ToArray());
        var request = GoogleGenerationProductionRuntimeRequestFactory.Create((await spend.LoadApprovedAsync(active.SubmissionEnvelope, TestContext.Current.CancellationToken))!,
            active.Snapshot, active.CurrentEstimateMinorUnits);
        await r.Service.ValidateReservedRuntimeBindingAsync(request.SubmissionEnvelope,
            ChirpResumeRuntimeBinding.Hash(request.Snapshot, request.Currency, request.Scale, request.CurrentEstimateMinorUnits),
            request.Currency, request.Scale, request.CurrentEstimateMinorUnits, TestContext.Current.CancellationToken);
        var changed = request.Snapshot with { AdmittedTrust = request.Snapshot.AdmittedTrust with { EndpointId = "changed" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Service.ValidateReservedRuntimeBindingAsync(request.SubmissionEnvelope,
            ChirpResumeRuntimeBinding.Hash(changed, request.Currency, request.Scale, request.CurrentEstimateMinorUnits),
            request.Currency, request.Scale, request.CurrentEstimateMinorUnits, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT Consumed AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }
}
