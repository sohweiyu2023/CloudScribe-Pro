using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Reopens the immutable failed checkpoint; never creates approval, modifies a journal or submits a request.</summary>
internal sealed class GoogleLongAudioFailedRangeReplacementResolver(GoogleLongAudioRequest root,
    GoogleLongAudioBudgetScope scope, string operations, string output)
{
    internal bool IsReplacement(GoogleLongAudioRequest request) =>
        scope.FailedRangeReplacements?.Any(x => x.ReplacementAttemptId == request.AttemptId) == true
        || scope.DeferredUnknownRanges?.Any(x => x.ExplicitRegeneration?.AttemptId == request.AttemptId) == true;

    internal GoogleLongAudioRequest? Resolve(string source, ChirpNarrationRange range, GoogleLongAudioRequest failed,
        GoogleLongAudioCheckpoint checkpoint)
    {
        var regeneration = GoogleLongAudioExplicitRegeneration.Resolve(source, range, root, failed, checkpoint, scope, operations);
        if (regeneration is not null) return regeneration;
        GoogleLongAudioFailedRangeReplacementPlanner.RequireScopeShape(scope);
        var matches = scope.FailedRangeReplacements?.Where(x => x.FailedAttemptId == failed.AttemptId).ToArray();
        if (matches is not { Length: > 0 }) return null;
        var approval = matches.Single();
        if (IsReplacement(failed)) throw new InvalidDataException("No automatic failed-range replacement chain is allowed.");
        string path = Path.Combine(operations, failed.AttemptId.ToString("N") + ".json");
        ChirpPolicyFiles.RejectLinks(path); ChirpPolicyFiles.RejectLinks(output);
        var saved = new GoogleLongAudioFileJournal(operations).Load(failed.AttemptId);
        if (saved != checkpoint) throw new InvalidDataException("The failed checkpoint changed after review.");
        using var bytes = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (bytes.Length > 16384) throw new InvalidDataException("The exact failed checkpoint exceeds its journal extent.");
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        bool hasOutput = false;
        foreach (string suffix in new[] { ".wav", ".object.json", ".verified.json", ".recovery-verified.json" })
        {
            string artifact = Path.Combine(output, failed.AttemptId.ToString("N") + suffix);
            ChirpPolicyFiles.RejectLinks(artifact); hasOutput |= File.Exists(artifact);
        }
        return GoogleLongAudioFailedRangeReplacementPlanner.Create(source, range, root, failed, checkpoint,
            hash, approval, hasOutput, scope.UsdPerMillionCharacters);
    }

    internal IReadOnlyList<GoogleLongAudioRequest> ReviewRequests(string source)
    {
        GoogleLongAudioFailedRangeReplacementPlanner.RequireScopeShape(scope);
        List<GoogleLongAudioRequest> requests = [];
        foreach (var item in scope.FailedRangeReplacements ?? [])
        {
            if (!string.Equals(item.RootBindingSha256, root.BindingSha256, StringComparison.Ordinal)) continue;
            var range = new ChirpNarrationRange(item.StartScalar, item.EndScalarExclusive, item.SourceSha256, null);
            string text = ChirpNarrationPlanner.Slice(source, range);
            var checkpoint = new GoogleLongAudioFileJournal(operations).Load(item.FailedAttemptId);
            var failed = GoogleLongAudioRequest.Create(item.FailedAttemptId, root.ProjectNumber, root.Location, root.CredentialReferenceId,
                checkpoint.OutputGcsUri, text, root.LanguageCode, root.VoiceName, "LINEAR16");
            requests.Add(Resolve(source, range, failed, checkpoint) ?? throw new InvalidDataException("Exact replacement approval is absent."));
        }
        foreach (var item in scope.DeferredUnknownRanges ?? [])
        {
            if (item.ExplicitRegeneration is null || !string.Equals(item.RootBindingSha256, root.BindingSha256, StringComparison.Ordinal)) continue;
            string text = string.Concat(source.EnumerateRunes().Skip(item.StartScalar).Take(item.EndScalarExclusive - item.StartScalar).Select(x => x.ToString()));
            var range = new ChirpNarrationRange(item.StartScalar, item.EndScalarExclusive, ArticleAudioManifest.HashText(text), null);
            var checkpoint = new GoogleLongAudioFileJournal(operations).Load(item.AttemptId);
            var original = GoogleLongAudioRequest.Create(item.AttemptId, root.ProjectNumber, root.Location, root.CredentialReferenceId,
                checkpoint.OutputGcsUri, text, root.LanguageCode, root.VoiceName, "LINEAR16");
            requests.Add(Resolve(source, range, original, checkpoint) ?? throw new InvalidDataException("Exact regeneration approval is absent."));
        }
        return requests.AsReadOnly();
    }
}
