using System.Text;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Revalidates pinned unknown evidence and disjoint deterministic work. Never writes or settles evidence.</summary>
internal sealed class GoogleLongAudioDeferredWork(GoogleLongAudioRequest root, GoogleLongAudioBudgetScope scope,
    string operations, string output, string ledgerPath, string scopeSha256, GoogleLongAudioRecoveryAuthority authority)
{
    internal GoogleLongAudioCheckpoint? InspectDeferred(GoogleLongAudioRequest request)
    {
        if (scope.DeferredUnknownRanges?.Any(x => x.AttemptId == request.AttemptId) != true) return null;
        RequireReady(request, newPost: false);
        return new GoogleLongAudioFileJournal(operations).Load(request.AttemptId);
    }

    internal void RequireReady(GoogleLongAudioRequest request, bool newPost) => RequireReady(request.AttemptId, newPost, request);

    internal void RequireReady(Guid current, bool newPost = false) => RequireReady(current, newPost, null);

    private void RequireReady(Guid current, bool newPost, GoogleLongAudioRequest? exact)
    {
        GoogleLongAudioDeferredRange.RequireScope(scope);
        var descriptors = scope.DeferredUnknownRanges;
        if (descriptors is null || descriptors.Count == 0 || descriptors.Select(x => x.AttemptId).Distinct().Count() != descriptors.Count)
            throw new InvalidDataException("No exact deferred-range continuation was reviewed.");
        var journal = new GoogleLongAudioFileJournal(operations);
        var ledger = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetLedger>(ReadPhysical(ledgerPath, 8 * 1024 * 1024));
        if (ledger.Version != 1 || !Same(ledger.ScopeSha256, scopeSha256) || ledger.Claims is null)
            throw new InvalidDataException("Deferred continuation ledger identity changed.");
        var resolved = authority.FindExact(current, scope, exact);
        var node = resolved.Part;
        foreach (var descriptor in descriptors)
        {
            if (descriptor.AttemptId == Guid.Empty || !Same(descriptor.RootBindingSha256, root.BindingSha256)
                || descriptor.StartScalar < 0 || descriptor.EndScalarExclusive <= descriptor.StartScalar)
                throw new InvalidDataException("Deferred range belongs to another source or has invalid coverage.");
            string path = Path.Combine(operations, descriptor.AttemptId.ToString("N") + ".json");
            ChirpPolicyFiles.RejectLinks(path);
            var saved = journal.Load(descriptor.AttemptId);
            var deferred = authority.FindExact(descriptor.AttemptId, scope);
            var claim = ledger.Claims.SingleOrDefault(x => x.AttemptId == descriptor.AttemptId);
            if (!Same(ChirpPolicyFiles.Hash(ReadPhysical(path, 8192)), descriptor.CheckpointSha256)
                || saved.State != GoogleLongAudioState.PostClaimed || saved.OperationName is not null
                || saved.OperationParseFailed || saved.HttpStatus != 0 || saved.RpcCode is not null
                || saved.Observation is not { ReadbackVerified: true } || saved.Observation.ReadState == CloudScribe.Domain.Generation.GoogleResponseReadState.Complete
                || deferred.Part.Range.StartScalar != descriptor.StartScalar || deferred.Part.Range.EndScalarExclusive != descriptor.EndScalarExclusive
                || !Same(saved.BindingSha256, deferred.Part.Request.BindingSha256) || claim is null
                || !Same(claim.RequestBindingSha256, saved.BindingSha256) || claim.MaximumUsdCents <= 0
                || claim.Recovery != deferred.Proof
                || !Same(ChirpPolicyFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(claim)), descriptor.HeldClaimSha256))
                throw new InvalidDataException("Pinned unknown evidence or its retained hold changed; reconcile before new work.");
            RequireNoDeferredOutput(descriptor.AttemptId);
            string guard = Path.Combine(operations, "observation-pending.guard");
            ChirpPolicyFiles.RejectLinks(guard);
            byte[] guardBytes = ReadPhysical(guard, 32);
            if (!Same(ChirpPolicyFiles.Hash(guardBytes), descriptor.LegacyGuardSha256)
                || !Same(Encoding.ASCII.GetString(guardBytes), descriptor.AttemptId.ToString("N")))
                throw new InvalidDataException("The preserved unknown observation guard changed.");
            bool approvedRegeneration = descriptor.ExplicitRegeneration is not null && resolved.RegenerationOwner == descriptor.AttemptId
                && current != descriptor.AttemptId && node.Range.StartScalar >= descriptor.StartScalar && node.Range.EndScalarExclusive <= descriptor.EndScalarExclusive;
            if (newPost && node.Range.StartScalar < descriptor.EndScalarExclusive && descriptor.StartScalar < node.Range.EndScalarExclusive && !approvedRegeneration)
                throw new InvalidDataException("New work overlaps an unresolved source interval; it cannot be billed again.");
        }
        RequireNoOtherEvidence(current, node.Request.BindingSha256);
    }

    private void RequireNoOtherEvidence(Guid current, string binding)
    {
        var journal = new GoogleLongAudioFileJournal(operations);
        foreach (string path in Directory.EnumerateFiles(operations, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
                throw new InvalidDataException("Unexpected operation evidence requires reconciliation.");
            var saved = journal.Load(id);
            if (scope.DeferredUnknownRanges!.Any(x => x.AttemptId == id)) continue;
            bool own = id == current && Same(saved.BindingSha256, binding);
            if (saved.OperationParseFailed || saved.State == GoogleLongAudioState.PostClaimed && !own
                || saved.Observation is not { ReadbackVerified: true, ReadState: CloudScribe.Domain.Generation.GoogleResponseReadState.Complete, Truncated: false }
                    && !(own && saved.State == GoogleLongAudioState.PostClaimed)
                    && !(own && saved.HasVerifiedPollDeadline)
                || saved.State == GoogleLongAudioState.Pollable && !own)
                throw new InvalidDataException("A new unapproved uncertain outcome stops distinct-range continuation.");
        }
        foreach (string guard in Directory.EnumerateFiles(operations, "*.observation-pending.guard"))
        {
            ChirpPolicyFiles.RejectLinks(guard);
            if (!Same(Path.GetFileName(guard), current.ToString("N") + ".observation-pending.guard")
                || !Same(Encoding.ASCII.GetString(ReadPhysical(guard, 32)), current.ToString("N")))
                throw new InvalidDataException("Another request's response evidence must be reconciled.");
        }
    }
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    private void RequireNoDeferredOutput(Guid attempt)
    {
        foreach (string suffix in new[] { ".wav", ".object.json", ".verified.json", ".recovery-verified.json" })
        {
            string path = Path.Combine(output, attempt.ToString("N") + suffix); ChirpPolicyFiles.RejectLinks(path);
            if (File.Exists(path)) throw new InvalidDataException("The unknown range has local output evidence; reconcile it before new work.");
        }
    }
    private static byte[] ReadPhysical(string path, int maximum)
    {
        ChirpPolicyFiles.RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 || file.Length > maximum) throw new InvalidDataException("Deferred evidence extent changed.");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Deferred evidence changed while reading.");
        return bytes;
    }
}
