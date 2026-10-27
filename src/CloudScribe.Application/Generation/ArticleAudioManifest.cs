using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Application.Generation;

public sealed record ArticleAudioManifest(
    Guid ParentId, Guid ParentRevisionId, string ParentSha256,
    IReadOnlyList<ArticleAudioMember> Members)
{
    public static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text)));

    public IReadOnlyList<ArticleAcceptedAudio> RequireComplete(IReadOnlyList<ArticleAcceptedAudio> accepted)
    {
        if (Members.Count is < 1 or > 200 || Members.Select(x => x.DocumentId).Distinct().Count() != Members.Count
            || !Members.Select(x => x.Ordinal).SequenceEqual(Enumerable.Range(1, Members.Count)))
            throw new InvalidOperationException("Article part order is incomplete or ambiguous.");
        List<ArticleAcceptedAudio> ordered = [];
        foreach (var member in Members)
        {
            var candidates = accepted.Where(x => x.DocumentId == member.DocumentId).ToArray();
            if (candidates.Length != 1 || candidates[0].RevisionId != member.RevisionId
                || !string.Equals(candidates[0].TextSha256, member.TextSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Article audio is missing, stale or ambiguous; no export was started.");
            ordered.Add(candidates[0]);
        }
        if (ordered.Select(x => x.LocalAttemptId).Distinct(StringComparer.Ordinal).Count() != ordered.Count
            || ordered.Select(x => x.AudioPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Count)
            throw new InvalidOperationException("Article audio contains duplicate output identities.");
        if (ordered.Any(x => !Guid.TryParseExact(x.LocalAttemptId, "N", out _) || string.IsNullOrWhiteSpace(x.VoiceName)
            || string.IsNullOrWhiteSpace(x.LanguageCode) || x.AudioBytes is < 12 or > 256L * 1024 * 1024)
            || ordered.Select(x => x.VoiceName).Distinct(StringComparer.Ordinal).Count() != 1
            || ordered.Select(x => x.LanguageCode).Distinct(StringComparer.Ordinal).Count() != 1
            || ordered.Sum(x => x.AudioBytes) > 2L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Article audio identities, voices or size are incompatible.");
        return ordered;
    }
}
