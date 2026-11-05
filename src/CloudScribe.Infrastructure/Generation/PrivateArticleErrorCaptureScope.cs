using System.Security.Cryptography;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit, in-memory article consent using the existing exact-request tickets and protected store.
/// Grants no spend/dispatch authority, never survives restart and never retries a request.</summary>
public sealed class PrivateArticleErrorCaptureScope : IDisposable
{
    private readonly GooglePrivateErrorDiagnostics diagnostics;
    private readonly TimeProvider clock;
    private readonly Guid owner;
    private readonly Guid jobId, documentId, revisionId;
    private readonly string plan, policy, review, account, project, voice;
    private readonly int maximumRequests;
    private readonly bool allowConfirmedErrorContinuation;
    private readonly bool completionBound;
    private readonly long started;
    private readonly HashSet<string> attached = new(StringComparer.Ordinal);
    private string? credential;
    private int? activeOrdinal;
    private string? activePayload;
    private bool stopped;
    private DateTimeOffset lastUtc;
    public DateTimeOffset? ExpiresUtc { get; }
    public bool EvidenceComplete { get; private set; } = true;

    public PrivateArticleErrorCaptureScope(GooglePrivateErrorDiagnostics diagnostics, TimeProvider clock, ChirpWholeArticleJob job,
        Guid revisionId, string account, string project, string voice, int maximumRequests, bool explicitlyConfirmed,
        bool allowConfirmedErrorContinuation = false)
    {
        ArgumentNullException.ThrowIfNull(diagnostics); ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(job);
        if (!explicitlyConfirmed || job.JobId == Guid.Empty || job.DocumentId == Guid.Empty || revisionId == Guid.Empty
            || job.State is not (ChirpWholeArticleState.Ready or ChirpWholeArticleState.Executing)
            || !ChirpPolicyValidation.IsHash(job.PlanSha256) || string.IsNullOrWhiteSpace(account)
            || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(voice) || maximumRequests is < 1 or > 10_000)
            throw new InvalidOperationException("Explicit bounded article-error capture consent and exact job identity are required.");
        this.diagnostics = diagnostics; this.clock = clock; jobId = job.JobId; documentId = job.DocumentId;
        this.revisionId = revisionId; plan = job.PlanSha256; policy = job.PolicySha256; review = job.ReviewSha256;
        this.account = account; this.project = project; this.voice = voice;
        this.maximumRequests = maximumRequests; this.allowConfirmedErrorContinuation = allowConfirmedErrorContinuation;
        completionBound = job.CompletionBound;
        started = clock.GetTimestamp(); lastUtc = clock.GetUtcNow();
        ExpiresUtc = completionBound ? null
            : job.ExpiresAtUtc < lastUtc.AddHours(6) ? job.ExpiresAtUtc : lastUtc.AddHours(6);
        if (ExpiresUtc is { } expiry && expiry <= clock.GetUtcNow()) throw new InvalidOperationException("Article capture cannot begin under expired authority.");
        owner = diagnostics.AcquireArticleCapture();
    }

    public void RequireReady(ChirpWholeArticleJob job)
    {
        var elapsed = clock.GetElapsedTime(started, clock.GetTimestamp());
        var now = clock.GetUtcNow();
        if (stopped || !EvidenceComplete || !diagnostics.OwnsArticleCapture(owner) || job.JobId != jobId || job.DocumentId != documentId
            || job.State is not (ChirpWholeArticleState.Ready or ChirpWholeArticleState.Executing)
            || !string.Equals(job.PlanSha256, plan, StringComparison.Ordinal) || !string.Equals(job.PolicySha256, policy, StringComparison.Ordinal)
            || !string.Equals(job.ReviewSha256, review, StringComparison.Ordinal) || job.CompletionBound != completionBound
            || ExpiresUtc is { } expiry && now >= expiry
            || now < lastUtc || elapsed < TimeSpan.Zero || !completionBound && elapsed >= TimeSpan.FromHours(6) || attached.Count >= maximumRequests)
            throw new PrivateDiagnosticNotReadyException();
        lastUtc = now;
        diagnostics.PreflightArticleStorage(owner); // Checked before the next reservation and again on exact ticket attachment.
    }

    public void Attach(ChirpWholeArticleJob job, int ordinal, GenerationProviderRequest request, GoogleGenerationSubmissionEnvelope envelope)
        => AttachExact(job, ordinal, request, envelope, ChirpArticleService.Key(plan, ordinal), null);

    public void AttachRecovery(ChirpWholeArticleJob job, ChirpArticleRecoveryChild child,
        GenerationProviderRequest request, GoogleGenerationSubmissionEnvelope envelope)
    {
        if (!allowConfirmedErrorContinuation || job.MaximumRecoveryRequests < 1 || child.JobId != jobId || child.CoverageOrdinal < 1
            || !string.Equals(child.PlanSha256, plan, StringComparison.Ordinal) || child.Depth < 1
            || !string.Equals(child.PayloadSha256, envelope.CompiledPayloadSha256, StringComparison.Ordinal)
            || child.PayloadBytes != request.CompiledPayload.Length)
            throw new InvalidOperationException("This capture scope does not cover bounded child recovery.");
        AttachExact(job, child.CoverageOrdinal, request, envelope, ChirpArticleService.RecoveryKey(child), child.AuthorizationId);
    }

    private void AttachExact(ChirpWholeArticleJob job, int ordinal, GenerationProviderRequest request,
        GoogleGenerationSubmissionEnvelope envelope, string expectedKey, Guid? expectedAuthorization)
    {
        RequireReady(job);
        string payloadHash = Convert.ToHexStringLower(SHA256.HashData(request.CompiledPayload.Span));
        if (ordinal < 1 || attached.Contains(expectedKey) || !string.Equals(request.IdempotencyKey, expectedKey, StringComparison.Ordinal)
            || !string.Equals(request.AccountId, account, StringComparison.Ordinal) || !string.Equals(envelope.AccountId, account, StringComparison.Ordinal)
            || !string.Equals(envelope.VoiceName, voice, StringComparison.Ordinal) || envelope.ChirpAuthorizationId is null
            || expectedAuthorization is not null && envelope.ChirpAuthorizationId != expectedAuthorization
            || !string.Equals(envelope.CompiledPayloadSha256, payloadHash, StringComparison.Ordinal)
            || envelope.CompiledPayloadBytes != request.CompiledPayload.Length
            || credential is not null && !string.Equals(credential, envelope.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The exact article capture request changed; no automatic capture or paid replay.");
        credential ??= envelope.CredentialReferenceId;
        var binding = new PrivateErrorCaptureBinding(documentId, revisionId, 0, account, project, voice, credential,
            payloadHash, PrivateErrorCaptureMode.ProtectedResponseEnvelope, jobId, ordinal, ExpiresUtc);
        diagnostics.AttachArticleCapture(owner, request, binding); attached.Add(expectedKey); activeOrdinal = ordinal; activePayload = payloadHash;
    }

    public async Task CompleteAsync(bool acceptedAndSaved, CancellationToken token, bool continueAfterConfirmedSentenceRejection = false)
    {
        try
        {
            await diagnostics.WaitForPendingWriteAsync(token).ConfigureAwait(false);
            // A saved provider outcome is independent of the advisory private sink, but missing evidence prevents the next paid request.
            EvidenceComplete = activeOrdinal is { } ordinal && activePayload is { } payloadHash
                && diagnostics.HasSavedArticleEvidence(jobId, ordinal, payloadHash);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { EvidenceComplete = false; }
        bool boundedRejection = !acceptedAndSaved && continueAfterConfirmedSentenceRejection && allowConfirmedErrorContinuation
            && activeOrdinal is { } failedOrdinal && activePayload is { } failedPayload
            && diagnostics.HasSavedCompleteArticleError(jobId, failedOrdinal, failedPayload);
        if (!EvidenceComplete || !acceptedAndSaved && !boundedRejection) Dispose();
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true; diagnostics.ReleaseArticleCapture(owner); GC.SuppressFinalize(this);
    }
}
