using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Validates a child by rebuilding the exact deterministic tree from the pinned root and canonical
/// terminal rejection journals. This is not an authority writer; existing scopes remain exact-request only.</summary>
internal sealed class GoogleLongAudioRecoveryAuthority(GoogleLongAudioRequest root, string operations, string output)
{
    internal GoogleLongAudioRecoveryProvenance RequireProvenance(GoogleLongAudioRequest request, GoogleLongAudioBudgetScope scope)
    {
        if (scope.AdaptiveRecoveryRootBindings?.Contains(root.BindingSha256, StringComparer.Ordinal) != true
            || !scope.ApprovedRequestCeilingsUsdCents.ContainsKey(root.BindingSha256)
            || !string.Equals(root.AudioEncoding, "LINEAR16", StringComparison.Ordinal))
            throw new InvalidDataException("This scope does not authorize article recovery for the exact root.");
        ChirpPolicyFiles.RejectLinks(operations); ChirpPolicyFiles.RejectLinks(output);
        string guard = Path.Combine(operations, "observation-pending.guard"); ChirpPolicyFiles.RejectLinks(guard);
        if (File.Exists(guard) && (new FileInfo(guard).Length != 32
            || !string.Equals(File.ReadAllText(guard), request.AttemptId.ToString("N"), StringComparison.Ordinal)))
            throw new InvalidDataException("Another provider observation requires reconciliation before recovery.");
        using var wire = JsonDocument.Parse(root.CopyPayload());
        string source = wire.RootElement.GetProperty("input").GetProperty("text").GetString()!;
        var journal = new GoogleLongAudioFileJournal(operations);
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
        Stack<(GoogleLongAudioRecoveryPart Part, GoogleLongAudioRecoveryProvenance? Proof)> pending = new();
        pending.Push((new(new(0, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(source), null), root, 0), null));
        while (pending.TryPop(out var item))
        {
            var node = item.Part;
            if (node.Request.AttemptId == request.AttemptId)
            {
                if (!string.Equals(node.Request.BindingSha256, request.BindingSha256, StringComparison.Ordinal)
                    || !node.Request.CopyPayload().AsSpan().SequenceEqual(request.CopyPayload()))
                    throw new InvalidDataException("The recovery child identity was rebound.");
                return item.Proof ?? throw new InvalidDataException("The root must use its exact reviewed admission.");
            }
            var saved = journal.ReadIfExists(node.Request.AttemptId);
            if (saved is null || saved.State != GoogleLongAudioState.Failed
                || saved.FailureKind != GoogleLongAudioFailureKind.SentenceLengthReported) continue;
            if (File.Exists(Path.Combine(output, node.Request.AttemptId.ToString("N") + ".verified.json"))
                || File.Exists(Path.Combine(output, node.Request.AttemptId.ToString("N") + ".recovery-verified.json")))
                throw new InvalidDataException("A rejected range also has accepted media; reconcile it before recovery.");
            foreach (var part in GoogleLongAudioRecoveryPlanner.Split(source, node.Range, node.Request, saved, false, scope.UsdPerMillionCharacters))
                pending.Push((part, new(root.BindingSha256, node.Request.BindingSha256, part.Range.StartScalar,
                    part.Range.EndScalarExclusive, GoogleLongAudioRecoveryPlanner.Version,
                    ChirpPolicyFiles.Hash(File.ReadAllBytes(Path.Combine(operations, node.Request.AttemptId.ToString("N") + ".json"))))));
        }
        throw new InvalidDataException("No retained confirmed rejection authorizes this exact recovery child.");
    }

    internal void RequireNoOtherPending(GoogleLongAudioRequest request)
    {
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
