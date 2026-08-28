using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class DurableGenerationReleaseFinalizer
{
    private readonly GenerationReleasePublisher _publisher;
    private readonly GenerationReleaseVerifier _verifier;
    private readonly AtomicJsonGenerationReleaseCheckpointStore _checkpointStore;
    private readonly TimeProvider _timeProvider;

    public DurableGenerationReleaseFinalizer(
        GenerationReleasePublisher publisher,
        GenerationReleaseVerifier verifier,
        AtomicJsonGenerationReleaseCheckpointStore checkpointStore,
        TimeProvider? timeProvider = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GenerationReleaseFinalizationResult> FinalizeAsync(
        GenerationCollectionReleaseDecision decision,
        string approvalId,
        string outputPath,
        IEnumerable<GenerationPublishedSegment> publishedSegments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(publishedSegments);

        var receipt = _publisher.Publish(decision, approvalId, outputPath, publishedSegments);
        var pending = GenerationReleaseCheckpoint.FromReceipt(
            receipt,
            GenerationReleaseCheckpointState.PublishedPendingVerification,
            _timeProvider.GetUtcNow());
        await _checkpointStore.SaveAsync(pending, cancellationToken).ConfigureAwait(false);

        return await VerifyAndFinalizeAsync(receipt, pending, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationReleaseFinalizationResult> RecoverAsync(
        GenerationReleaseReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.Verify())
            throw new InvalidDataException("Cannot recover release finalization from an invalid receipt.");

        var checkpoint = await _checkpointStore.ReadAsync(receipt.CollectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No durable release checkpoint exists for the recovered receipt.");
        checkpoint.EnsureMatches(receipt);

        var verification = _verifier.Verify(receipt);
        if (!verification.IsValid)
            throw new InvalidDataException($"Recovered release failed disk verification: {verification.DiagnosticCode}");

        if (checkpoint.State == GenerationReleaseCheckpointState.Finalized)
            return new GenerationReleaseFinalizationResult(receipt, verification);

        return await VerifyAndFinalizeAsync(receipt, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GenerationReleaseFinalizationResult> VerifyAndFinalizeAsync(
        GenerationReleaseReceipt receipt,
        GenerationReleaseCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var verification = _verifier.Verify(receipt);
        if (!verification.IsValid)
            throw new InvalidDataException($"Published release failed integrity verification: {verification.DiagnosticCode}");

        var finalized = checkpoint.MarkFinalized(receipt, verification, _timeProvider.GetUtcNow());
        await _checkpointStore.SaveAsync(finalized, cancellationToken).ConfigureAwait(false);
        return new GenerationReleaseFinalizationResult(receipt, verification);
    }
}
