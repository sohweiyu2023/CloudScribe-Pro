using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Validates a child by rebuilding the exact deterministic tree from the pinned root and canonical
/// terminal rejection journals. This is not an authority writer; existing scopes remain exact-request only.</summary>
internal sealed class GoogleLongAudioRecoveryAuthority(GoogleLongAudioRequest root, string operations, string output)
{
    internal GoogleLongAudioDeferredWork? Deferred { get; set; }
    internal GoogleLongAudioRecoveryProvenance RequireProvenance(GoogleLongAudioRequest request, GoogleLongAudioBudgetScope scope)
    {
        if (scope.AdaptiveRecoveryRootBindings?.Contains(root.BindingSha256, StringComparer.Ordinal) != true
            || !scope.ApprovedRequestCeilingsUsdCents.ContainsKey(root.BindingSha256)
            || !string.Equals(root.AudioEncoding, "LINEAR16", StringComparison.Ordinal))
            throw new InvalidDataException("This scope does not authorize article recovery for the exact root.");
        ChirpPolicyFiles.RejectLinks(operations); ChirpPolicyFiles.RejectLinks(output);
        string guard = Path.Combine(operations, "observation-pending.guard"); ChirpPolicyFiles.RejectLinks(guard);
        if (Deferred is null && File.Exists(guard) && (new FileInfo(guard).Length != 32
            || !string.Equals(File.ReadAllText(guard), request.AttemptId.ToString("N"), StringComparison.Ordinal)))
            throw new InvalidDataException("Another provider observation requires reconciliation before recovery.");
        GoogleLongAudioFailedRangeReplacementPlanner.RequireScopeShape(scope);
        var journal = new GoogleLongAudioFileJournal(operations);
        RequireResolvedEvidence(request, journal);
        return FindExact(request.AttemptId, scope, request).Proof
            ?? throw new InvalidDataException("The root must use its exact reviewed admission.");
    }

    internal (GoogleLongAudioRecoveryPart Part, GoogleLongAudioRecoveryProvenance? Proof, Guid? RegenerationOwner) FindExact(
        Guid attemptId, GoogleLongAudioBudgetScope scope, GoogleLongAudioRequest? exact = null)
    {
        using var wire = JsonDocument.Parse(root.CopyPayload());
        string source = wire.RootElement.GetProperty("input").GetProperty("text").GetString()!;
        var replacements = new GoogleLongAudioFailedRangeReplacementResolver(root, scope, operations, output);
        var journal = new GoogleLongAudioFileJournal(operations);
        Stack<(GoogleLongAudioRecoveryPart Part, GoogleLongAudioRecoveryProvenance? Proof, Guid? RegenerationOwner)> pending = new();
        pending.Push((new(new(0, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(source), null), root, 0), null, null));
        while (pending.TryPop(out var item))
        {
            var node = item.Part;
            if (node.Request.AttemptId == attemptId)
            {
                if (exact is not null && (!string.Equals(node.Request.BindingSha256, exact.BindingSha256, StringComparison.Ordinal)
                    || !node.Request.CopyPayload().AsSpan().SequenceEqual(exact.CopyPayload())))
                    throw new InvalidDataException("The recovery child identity was rebound.");
                return (node, item.Proof, item.RegenerationOwner);
            }
            var saved = journal.ReadIfExists(node.Request.AttemptId);
            if (saved?.State == GoogleLongAudioState.PostClaimed)
            {
                var regenerated = GoogleLongAudioExplicitRegeneration.Resolve(source, node.Range, root, node.Request, saved, scope, operations);
                if (regenerated is not null)
                {
                    var descriptor = scope.DeferredUnknownRanges!.Single(x => x.AttemptId == node.Request.AttemptId);
                    pending.Push((new(node.Range, regenerated, descriptor.ExplicitRegeneration!.MaximumUsdCents), new(root.BindingSha256, node.Request.BindingSha256,
                        node.Range.StartScalar, node.Range.EndScalarExclusive, GoogleLongAudioExplicitRegeneration.PlannerVersion, descriptor.CheckpointSha256), descriptor.AttemptId));
                    continue;
                }
            }
            if (saved is null || saved.State != GoogleLongAudioState.Failed
                || replacements.IsReplacement(node.Request) && saved.FailureKind != GoogleLongAudioFailureKind.SentenceLengthReported) continue;
            var replacement = replacements.Resolve(source, node.Range, node.Request, saved);
            if (replacement is not null)
            {
                var approval = scope.FailedRangeReplacements!.Single(x => x.FailedAttemptId == node.Request.AttemptId);
                pending.Push((new(node.Range, replacement, 0), new(root.BindingSha256, node.Request.BindingSha256,
                    node.Range.StartScalar, node.Range.EndScalarExclusive, GoogleLongAudioRecoveryPlanner.Version, approval.FailedCheckpointSha256), item.RegenerationOwner));
                continue;
            }
            if (saved.FailureKind != GoogleLongAudioFailureKind.SentenceLengthReported) continue;
            if (File.Exists(Path.Combine(output, node.Request.AttemptId.ToString("N") + ".verified.json"))
                || File.Exists(Path.Combine(output, node.Request.AttemptId.ToString("N") + ".recovery-verified.json")))
                throw new InvalidDataException("A rejected range also has accepted media; reconcile it before recovery.");
            foreach (var part in GoogleLongAudioRecoveryPlanner.Split(source, node.Range, node.Request, saved, false, scope.UsdPerMillionCharacters))
                pending.Push((part, new(root.BindingSha256, node.Request.BindingSha256, part.Range.StartScalar,
                    part.Range.EndScalarExclusive, GoogleLongAudioRecoveryPlanner.Version,
                    ChirpPolicyFiles.Hash(File.ReadAllBytes(Path.Combine(operations, node.Request.AttemptId.ToString("N") + ".json")))), item.RegenerationOwner));
        }
        throw new InvalidDataException("No retained confirmed rejection authorizes this exact recovery child.");
    }

    private void RequireResolvedEvidence(GoogleLongAudioRequest request, GoogleLongAudioFileJournal journal)
    {
        if (Deferred is not null) { Deferred.RequireReady(request, newPost: false); return; }
        foreach (string path in Directory.EnumerateFiles(operations, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
                throw new InvalidDataException("Unexpected operation evidence requires reconciliation.");
            var state = journal.Load(id);
            bool ownedClaim = id == request.AttemptId && string.Equals(state.BindingSha256, request.BindingSha256, StringComparison.Ordinal);
            if (state.State == GoogleLongAudioState.PostClaimed && !ownedClaim
                || state.OperationParseFailed || state.Observation is not { ReadbackVerified: true } && !(ownedClaim && state.State == GoogleLongAudioState.PostClaimed))
                throw new InvalidDataException("Unresolved provider evidence prevents a new recovery admission.");
        }
    }

    internal void RequireNoOtherPending(GoogleLongAudioRequest request)
    {
        if (Deferred is not null) { Deferred.RequireReady(request, newPost: true); return; }
        var journal = new GoogleLongAudioFileJournal(operations);
        foreach (string path in Directory.EnumerateFiles(operations, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
                throw new InvalidDataException("Unexpected operation evidence requires reconciliation.");
            var state = journal.Load(id);
            if (id != request.AttemptId && state.State is GoogleLongAudioState.PostClaimed or GoogleLongAudioState.Pollable)
                throw new InvalidDataException("Resume the existing provider operation before any new recovery POST.");
        }
    }
}
