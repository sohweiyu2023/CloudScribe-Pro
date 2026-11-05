using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public async Task ExplicitRecoveryCaptureAllowsDistinctChildAtSameOrdinalWithoutAnErrorCounterStop()
    {
        using var f = new Fixture(); var job = ArticleJob(f) with { MaximumRecoveryRequests = 2, MaximumRecoveryMinorUnits = 2 };
        using var capture = new PrivateArticleErrorCaptureScope(f.Store, f.Clock, job, f.Binding.RevisionId,
            f.Binding.AccountId, f.Binding.ProjectId, f.Binding.VoiceId, 3, true, allowConfirmedErrorContinuation: true);
        f.Handler.Body = JsonSerializer.Serialize(new { error = new { code = 400, status = "INVALID_ARGUMENT", message = "This request contains sentences that are too long. A unique retained explanation." } });
        var first = ArticleRequest(f, job, 1); var envelope = ArticleEnvelope(f);
        capture.Attach(job, 1, first, envelope);
        _ = await f.Provider.SubmitAsync(first, TestContext.Current.CancellationToken);
        await capture.CompleteAsync(false, TestContext.Current.CancellationToken, continueAfterConfirmedSentenceRejection: true);
        Assert.True(capture.EvidenceComplete); capture.RequireReady(job);
        var child = CaptureChild(f, job, envelope.ChirpAuthorizationId!.Value);
        var childRequest = new GenerationProviderRequest(first.ProviderStableId, first.OperationStableId, first.AccountId,
            ChirpArticleService.RecoveryKey(child), first.CompiledPayload, first.OutputFormat);
        capture.AttachRecovery(job, child, childRequest, envelope with { ChirpAuthorizationId = child.AuthorizationId });
        _ = await f.Provider.SubmitAsync(childRequest, TestContext.Current.CancellationToken);
        await capture.CompleteAsync(false, TestContext.Current.CancellationToken, continueAfterConfirmedSentenceRejection: true);
        Assert.Equal(2, f.Handler.Calls); Assert.Equal(2, f.Store.ReadRecords().Count);
        capture.RequireReady(job);
        // Privacy binding test only: fake equal payloads here grant no native subdivision/spend authority.
    }

    [Fact]
    public async Task TruncatedPrivateErrorCannotKeepRecoveryCaptureReady()
    {
        using var f = new Fixture(); var job = ArticleJob(f) with { MaximumRecoveryRequests = 2 };
        using var capture = new PrivateArticleErrorCaptureScope(f.Store, f.Clock, job, f.Binding.RevisionId,
            f.Binding.AccountId, f.Binding.ProjectId, f.Binding.VoiceId, 3, true, allowConfirmedErrorContinuation: true);
        f.Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = "Oversized synthetic detail " + new string('x', 20_000) } });
        var request = ArticleRequest(f, job, 1); capture.Attach(job, 1, request, ArticleEnvelope(f));
        _ = await f.Provider.SubmitAsync(request, TestContext.Current.CancellationToken);
        await capture.CompleteAsync(false, TestContext.Current.CancellationToken, continueAfterConfirmedSentenceRejection: true);
        Assert.True(Assert.Single(f.Store.ReadRecords()).Truncated);
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job)); Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public void ChildCaptureRejectsChangedWireBeforeAnyFakeProviderCall()
    {
        using var f = new Fixture(); var job = ArticleJob(f) with { MaximumRecoveryRequests = 2 };
        using var capture = new PrivateArticleErrorCaptureScope(f.Store, f.Clock, job, f.Binding.RevisionId,
            f.Binding.AccountId, f.Binding.ProjectId, f.Binding.VoiceId, 3, true, allowConfirmedErrorContinuation: true);
        var envelope = ArticleEnvelope(f); var child = CaptureChild(f, job, envelope.ChirpAuthorizationId!.Value) with { PayloadSha256 = new string('b', 64) };
        var request = ArticleRequest(f, job, 1);
        Assert.Throws<InvalidOperationException>(() => capture.AttachRecovery(job, child, request, envelope)); Assert.Equal(0, f.Handler.Calls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(64, true)]
    public void RecoveryCaptureRequiresPositiveDepthWhileNativeAuthorityOwnsStructuralTermination(int depth, bool allowed)
    {
        using var f = new Fixture(); var job = ArticleJob(f) with { MaximumRecoveryRequests = 32 };
        using var capture = new PrivateArticleErrorCaptureScope(f.Store, f.Clock, job, f.Binding.RevisionId,
            f.Binding.AccountId, f.Binding.ProjectId, f.Binding.VoiceId, 3, true, allowConfirmedErrorContinuation: true);
        var envelope = ArticleEnvelope(f);
        var child = CaptureChild(f, job, envelope.ChirpAuthorizationId!.Value) with { Depth = depth };
        var request = new GenerationProviderRequest(f.Request.ProviderStableId, f.Request.OperationStableId,
            f.Request.AccountId, ChirpArticleService.RecoveryKey(child), f.Request.CompiledPayload, f.Request.OutputFormat);
        if (allowed)
        {
            capture.AttachRecovery(job, child, request, envelope with { ChirpAuthorizationId = child.AuthorizationId });
            f.Provider.RequirePrivateDiagnosticsReady(request);
        }
        else Assert.Throws<InvalidOperationException>(() => capture.AttachRecovery(job, child, request,
            envelope with { ChirpAuthorizationId = child.AuthorizationId }));
        Assert.Equal(0, f.Handler.Calls); // Capture readiness is not paid/native subdivision authority.
    }

    private static ChirpArticleRecoveryChild CaptureChild(Fixture f, ChirpWholeArticleJob job, Guid parent) =>
        new(Guid.NewGuid(), job.JobId, parent, parent, job.PlanSha256, 1, 1, 1,
            new ChirpNarrationRange(0, 1, new string('c', 64), null), f.Binding.PayloadSha256, f.Request.CompiledPayload.Length, new string('d', 64));
}
