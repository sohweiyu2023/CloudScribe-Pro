using CloudScribe.App.Composition;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Theory]
    [InlineData("revision", LocalRequestReason.ReservedRevisionChanged)]
    [InlineData("capability", LocalRequestReason.ReservedEnvelopeChanged)]
    [InlineData("payload", LocalRequestReason.ReservedPayloadChanged)]
    [InlineData("source", LocalRequestReason.ReservedSourceChanged)]
    public async Task ReservedCaptureRejectsChangedEvidenceWithExactSafeInvariantAndNoMutation(string change, LocalRequestReason expected)
    {
        using var f = new Fixture(); await f.InitializeChirpAsync();
        var articles = f.CreateArticles();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles));
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var review = await preparation.PrepareAsync(f.Selection, 1, TestContext.Current.CancellationToken);
        await preparation.ApproveAsync(f.Selection, ChirpConfirm(review), TestContext.Current.CancellationToken);
        var original = (await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken))!;
        string before = await ReadReservedRowsAsync(f);
        if (string.Equals(change, "revision", StringComparison.Ordinal)) f.ChirpCompiler.Revision++;
        if (string.Equals(change, "capability", StringComparison.Ordinal)) f.ChirpCompiler.Capability = "new-catalog";
        if (string.Equals(change, "payload", StringComparison.Ordinal)) f.ChirpCompiler.PayloadSuffix = " private synthetic changed text";
        var selection = string.Equals(change, "source", StringComparison.Ordinal) ? f.Selection with { ExactText = f.Selection.ExactText + " private changed text" } : f.Selection;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.CompileReadonlyAsync(selection, TestContext.Current.CancellationToken));
        var details = LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow);
        Assert.Equal(expected, details.Reason); Assert.True(details.EvidenceComplete);
        Assert.DoesNotContain("private changed text", LocalRequestDiagnostics.UserMessage(details), StringComparison.Ordinal);
        Assert.Equal(before, await ReadReservedRowsAsync(f));
        Assert.Same(original, await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(42, original.SubmissionEnvelope.RequestRevision);
    }

    private static async Task<string> ReadReservedRowsAsync(Fixture f)
    {
        using var db = f.CreateDbContext();
        var attempts = await db.Database.SqlQueryRaw<string>("SELECT json_object('id',AuthorizationId,'envelope',EnvelopeJson,'consumed',Consumed,'reserved',ReservedMinorUnits,'financial',ApprovedFinancialSha256) AS Value FROM chirp_article_attempts ORDER BY AuthorizationId").ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        var queue = await db.Database.SqlQueryRaw<string>("SELECT json_object('key',IdempotencyKey,'submitted',SubmissionAttempted,'unresolved',UnresolvedSubmission,'local',LocalAttemptId,'provider',ProviderRequestId) AS Value FROM google_generation_queue_states ORDER BY IdempotencyKey").ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return string.Join('\n', attempts.Concat(queue));
    }
}
