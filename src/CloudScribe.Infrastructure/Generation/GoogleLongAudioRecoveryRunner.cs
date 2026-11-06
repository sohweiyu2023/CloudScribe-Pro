using System.Text.Json;
using System.Text;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Adaptive control flow over the existing independently admitted POST-once/poll/download boundary.
/// Deterministic child identities rebuild the same tree on restart. No policy mutation, retry loop or voice fallback.</summary>
public sealed class GoogleLongAudioRecoveryRunner(
    Func<GoogleLongAudioRequest, bool, CancellationToken, Task<GoogleLongAudioJobProgress>> execute,
    Func<GoogleLongAudioRequest, CancellationToken, Task<GoogleLongAudioRecoveryAllowance>> allowance,
    decimal usdPerMillionCharacters, TimeProvider clock, Action<GoogleLongAudioRecoveryTrace>? observe = null)
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One serialized iterative owner preserves exact source order, safe progress, stop and durable provider outcomes without recursive retry or a split-depth cap.")]
    public async Task<GoogleLongAudioRecoveryRun> RunAsync(GoogleLongAudioRequest root, GoogleLongAudioRunControl control,
        IProgress<GoogleLongAudioDocumentProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(control);
        if (!string.Equals(root.AudioEncoding, "LINEAR16", StringComparison.Ordinal)) throw new InvalidOperationException("Adaptive Long Audio requires supported LINEAR16 output; no format fallback is automatic.");
        using var payload = JsonDocument.Parse(root.CopyPayload());
        string source = payload.RootElement.GetProperty("input").GetProperty("text").GetString()!;
        var first = new ChirpNarrationRange(0, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(source), null);
        Stack<(ChirpNarrationRange Range, GoogleLongAudioRequest Request, Guid? Parent, int Depth)> pending = new(); pending.Push((first, root, null, 0));
        List<GoogleLongAudioRecoveryOutput> outputs = [];
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (control.IsStopRequested) return new(false, outputs.AsReadOnly(), null);
            Trace(LongAudioRecoveryStage.InspectOrSubmit, null);
            progress?.Report(new(LongAudioWorkflowStage.SubmittingOrResuming, null, outputs.Count, node.Range.StartScalar, first.EndScalarExclusive));
            var result = await execute(node.Request, false, token).ConfigureAwait(false);
            while (result.Checkpoint.State == GoogleLongAudioState.Pollable)
            {
                if (result.Checkpoint.HttpStatus is < 200 or >= 300 || result.Checkpoint.Observation is not { ReadbackVerified: true, Truncated: false })
                    return new(false, outputs.AsReadOnly(), result.Checkpoint);
                progress?.Report(new(LongAudioWorkflowStage.WaitingForGoogle, result.Checkpoint));
                await control.WaitBeforePollAsync(clock, token).ConfigureAwait(false);
                if (control.IsStopRequested) return new(false, outputs.AsReadOnly(), result.Checkpoint);
                result = await execute(node.Request, true, token).ConfigureAwait(false);
            }
            if (result.Artifact is { } audio)
            {
                if (result.Checkpoint.State != GoogleLongAudioState.CompletedUnverified || !string.Equals(audio.Identity.BindingSha256, node.Request.BindingSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Verified recovery output belongs to another request.");
                outputs.Add(new(node.Range, audio)); Trace(LongAudioRecoveryStage.VerifiedRange, null);
                progress?.Report(new(LongAudioWorkflowStage.CheckingGoogleAndOutput, result.Checkpoint, outputs.Count, node.Range.EndScalarExclusive, first.EndScalarExclusive)); continue;
            }
            progress?.Report(new(LongAudioWorkflowStage.Failed, result.Checkpoint));
            if (result.Checkpoint.State != GoogleLongAudioState.Failed || result.Checkpoint.FailureKind != GoogleLongAudioFailureKind.SentenceLengthReported)
            { Trace(LongAudioRecoveryStage.NeedsAttention, result.Checkpoint.FailureKind); return new(false, outputs.AsReadOnly(), result.Checkpoint); }
            var current = await allowance(node.Request, token).ConfigureAwait(false);
            if (current.HasAcceptedParentOutput) throw new InvalidDataException("A failed parent also has accepted output; reconcile instead of subdividing.");
            var parts = GoogleLongAudioRecoveryPlanner.Split(source, node.Range, node.Request, result.Checkpoint,
                current.HasPendingOrUncertain, usdPerMillionCharacters);
            Trace(LongAudioRecoveryStage.SplitConfirmedRejection, result.Checkpoint.FailureKind);
            // Reverse push gives left-to-right execution and output ordering, regardless of subdivision depth.
            for (int i = parts.Count - 1; i >= 0; i--) pending.Push((parts[i].Range, parts[i].Request, node.Request.AttemptId, checked(node.Depth + 1)));

            void Trace(LongAudioRecoveryStage stage, GoogleLongAudioFailureKind? kind) => observe?.Invoke(new(root.AttemptId, node.Request.AttemptId, node.Parent,
                node.Range.StartScalar, node.Range.EndScalarExclusive, node.Depth, node.Request.PayloadSha256, stage, kind));
        }
        RequireCoverage(source, first.EndScalarExclusive, outputs);
        return new(true, outputs.AsReadOnly(), null);
    }

    private static void RequireCoverage(string source, int end, List<GoogleLongAudioRecoveryOutput> outputs)
    {
        int cursor = 0;
        foreach (var item in outputs)
        {
            if (item.Range.StartScalar != cursor) throw new InvalidDataException("Recovery output coverage has a gap or overlap.");
            _ = ChirpNarrationPlanner.Slice(source, item.Range); cursor = item.Range.EndScalarExclusive;
        }
        if (cursor != end) throw new InvalidDataException("Recovery output coverage is incomplete.");
    }
}
