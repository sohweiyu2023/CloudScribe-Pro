using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Theory]
    [InlineData("ok")]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("throw")]
    [InlineData("payload")]
    [InlineData("correlation")]
    [InlineData("id")]
    [InlineData("message")]
    [InlineData("duplicate")]
    public async Task ExactChildReadbackIsRequiredBeforeArticleCaptureCanContinue(string mode)
    {
        var persistence = new ChildReadbackStore(mode); using var f = new Fixture(persistence);
        var job = ArticleJob(f) with { MaximumRecoveryRequests = 2, MaximumRecoveryMinorUnits = 2 };
        using var capture = new PrivateArticleErrorCaptureScope(f.Store, f.Clock, job, f.Binding.RevisionId,
            f.Binding.AccountId, f.Binding.ProjectId, f.Binding.VoiceId, 3, true, allowConfirmedErrorContinuation: true);
        f.Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = "This request contains sentences that are too long. A retained synthetic explanation." } });
        var parentEnvelope = ArticleEnvelope(f); var parentRequest = ArticleRequest(f, job, 1);
        capture.Attach(job, 1, parentRequest, parentEnvelope);
        _ = await f.Provider.SubmitAsync(parentRequest, TestContext.Current.CancellationToken);
        await capture.CompleteAsync(false, TestContext.Current.CancellationToken, continueAfterConfirmedSentenceRejection: true);
        Assert.True(capture.EvidenceComplete); capture.RequireReady(job);
        persistence.MutateNext = true;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = "SYNTHETIC DISTINCT CHILD TEXT" } });
        string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var child = CaptureChild(f, job, parentEnvelope.ChirpAuthorizationId!.Value) with { PayloadSha256 = hash, PayloadBytes = payload.Length };
        var request = new GenerationProviderRequest(parentRequest.ProviderStableId, parentRequest.OperationStableId, parentRequest.AccountId,
            ChirpArticleService.RecoveryKey(child), payload, parentRequest.OutputFormat);
        capture.AttachRecovery(job, child, request, parentEnvelope with
        { ChirpAuthorizationId = child.AuthorizationId, CompiledPayloadSha256 = hash, CompiledPayloadBytes = payload.Length });
        var response = await f.Provider.SubmitAsync(request, TestContext.Current.CancellationToken);
        await capture.CompleteAsync(false, TestContext.Current.CancellationToken, continueAfterConfirmedSentenceRejection: true);
        Assert.Equal("google-http-400", response.DiagnosticCode); Assert.Equal(2, f.Handler.Calls);
        if (string.Equals(mode, "ok", StringComparison.Ordinal))
        { Assert.True(capture.EvidenceComplete); capture.RequireReady(job); Assert.Equal(GoogleExplanationRetentionState.SavedPrivately, f.Store.PersistenceOutcome); }
        else
        { Assert.False(capture.EvidenceComplete); Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job)); Assert.Equal(GoogleExplanationRetentionState.StorageFailed, f.Store.PersistenceOutcome); }
        // Actual capture/provider child path with fake HTTP; this test grants no native subdivision or financial authority.
    }

    private sealed class ChildReadbackStore(string mode) : IPrivateProviderErrorPersistence
    {
        private readonly List<PrivateProviderErrorRecord> _records = [];
        private bool _changed;
        public bool MutateNext { get; set; }
        public bool PrepareProtectedCapture() => true;
        public bool Save(PrivateProviderErrorRecord record)
        {
            if (!MutateNext) { _records.Add(record); return true; }
            _changed = true;
            switch (mode)
            {
                case "missing": return true;
                case "payload": record = record with { PayloadSha256 = new string('e', 64) }; break;
                case "correlation": record = record with { TransportCorrelation = Guid.NewGuid().ToString("N") }; break;
                case "id": record = record with { Id = Guid.NewGuid() }; break;
                case "message": record = record with { Message = "Changed synthetic private record" }; break;
                case "duplicate": _records.Add(record); break;
            }
            _records.Add(record); return true;
        }
        public IReadOnlyList<PrivateProviderErrorRecord> Read() => _records;
        public PrivateProviderErrorReadResult ReadWithStatus()
        {
            if (_changed && string.Equals(mode, "throw", StringComparison.Ordinal)) throw new IOException("Synthetic private read failed.");
            return new(_changed && string.Equals(mode, "unreadable", StringComparison.Ordinal)
                ? PrivateProviderErrorReadStatus.CorruptOrUnprotectable : PrivateProviderErrorReadStatus.Success, _records);
        }
    }
}
