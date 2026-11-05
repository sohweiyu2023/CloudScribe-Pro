using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GooglePrivateErrorDiagnostics
{
    internal Guid AcquireArticleCapture()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_articleCaptureOwner is not null || _arm is not null || _ticket is not null || _active is not null || !_pendingWrite.IsCompleted)
                throw new InvalidOperationException("Another private capture is active or still saving; preserve it before reviewing article capture.");
            return (_articleCaptureOwner = Guid.NewGuid()).Value;
        }
    }

    private void RequireArticleOwner(Guid? owner)
    {
        if (_articleCaptureOwner != owner)
            throw new InvalidOperationException("Private article capture consent changed. Stop before another request.");
    }

    internal void AttachArticleCapture(Guid owner, GenerationProviderRequest request, PrivateErrorCaptureBinding binding)
    {
        lock (_gate)
        {
            RequireArticleOwner(owner);
            if (binding.CaptureMode != PrivateErrorCaptureMode.ProtectedResponseEnvelope)
                throw new InvalidOperationException("Article error evidence requires protected response capture.");
            ArmOwned(binding, explicitlyConfirmed: true, owner);
            Volatile.Write(ref _lastArticlePersistence, null);
            Attach(request, binding);
            if (!IsReadyFor(request, binding.CredentialReferenceId, requireProtectedEnvelope: true))
                throw new PrivateDiagnosticNotReadyException();
            _requiredArticleRequests.GetValue(request, static _ => new object());
        }
    }

    internal bool RequiresArticleCapture(GenerationProviderRequest request) => _requiredArticleRequests.TryGetValue(request, out _);
    internal bool OwnsArticleCapture(Guid owner) { lock (_gate) return !_disposed && _articleCaptureOwner == owner; }
    internal bool HasSavedArticleEvidence(Guid job, int ordinal, string payloadHash)
    {
        var evidence = Volatile.Read(ref _lastArticlePersistence);
        return evidence is { Saved: true } && evidence.JobId == job && evidence.Ordinal == ordinal
            && string.Equals(evidence.PayloadSha256, payloadHash, StringComparison.Ordinal);
    }
    internal bool HasSavedCompleteArticleError(Guid job, int ordinal, string payloadHash)
    {
        var evidence = Volatile.Read(ref _lastArticlePersistence);
        return evidence is { Saved: true, CompleteError: true } && evidence.JobId == job && evidence.Ordinal == ordinal
            && string.Equals(evidence.PayloadSha256, payloadHash, StringComparison.Ordinal);
    }
    internal void PreflightArticleStorage(Guid owner)
    {
        lock (_gate)
        {
            RequireArticleOwner(owner);
            if (!persistence.PrepareProtectedCapture()) throw new PrivateDiagnosticNotReadyException();
        }
    }

    internal void ReleaseArticleCapture(Guid owner)
    {
        lock (_gate)
        {
            if (_articleCaptureOwner != owner) return;
            ++_reviewEpoch; _arm = null; _ticket = null; _active = null; _articleCaptureOwner = null;
        }
    }
}
