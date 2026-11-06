using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Long Audio first; lossless subdivision only after exact confirmed sentence rejection.
/// Does not admit requests, mutate a policy, release a hold or retry a failed payload.</summary>
public static class GoogleLongAudioRecoveryPlanner
{
    public const string Version = "long-audio-natural-recovery-v1";

    public static IReadOnlyList<GoogleLongAudioRecoveryPart> Split(string source, ChirpNarrationRange range,
        GoogleLongAudioRequest parent, GoogleLongAudioCheckpoint rejected, bool hasPendingOrUncertain,
        decimal usdPerMillionCharacters)
    {
        ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(rejected);
        if (hasPendingOrUncertain || range.ReusedReceiptSha256 is not null || !string.Equals(parent.AudioEncoding, "LINEAR16", StringComparison.Ordinal)
            || rejected.State != GoogleLongAudioState.Failed || rejected.FailureKind != GoogleLongAudioFailureKind.SentenceLengthReported
            || rejected.OperationParseFailed || rejected.AttemptId != parent.AttemptId || !string.Equals(rejected.BindingSha256, parent.BindingSha256, StringComparison.Ordinal)
            || !string.Equals(rejected.PayloadSha256, parent.PayloadSha256, StringComparison.Ordinal) || rejected.PayloadBytes != parent.PayloadBytes
            || !string.Equals(rejected.OutputGcsUri, parent.OutputGcsUri, StringComparison.Ordinal)
            || !(rejected.HttpStatus == 400 && rejected.RpcCode is null && rejected.OperationName is null
                || rejected.HttpStatus == 200 && rejected.RpcCode == 3 && GoogleLongAudioValidation.IsOperation(rejected.OperationName, parent.ProjectNumber, parent.Location))
            || rejected.Observation is not
            {
                ReadbackVerified: true, ReadState: GoogleResponseReadState.Complete, Truncated: false,
                Retention: GoogleExplanationRetentionState.SavedPrivately
            } || usdPerMillionCharacters < 0)
            throw new InvalidOperationException("A retained exact terminal sentence rejection is required; no request was created.");
        string text = ChirpNarrationPlanner.Slice(source, range);
        using var wire = JsonDocument.Parse(parent.CopyPayload());
        if (!string.Equals(wire.RootElement.GetProperty("input").GetProperty("text").GetString(), text, StringComparison.Ordinal))
            throw new InvalidDataException("The rejected payload does not contain this exact source range.");
        int cut = NaturalCut(text);
        int leftCount = text[..cut].EnumerateRunes().Count();
        var children = new[] { Child(text[..cut], range.StartScalar, range.StartScalar + leftCount),
            Child(text[cut..], range.StartScalar + leftCount, range.EndScalarExclusive) };
        if (children[0].Range.EndScalarExclusive <= range.StartScalar || children[1].Range.StartScalar >= range.EndScalarExclusive
            || !string.Equals(string.Concat(children.Select(x => ChirpNarrationPlanner.Slice(source, x.Range))), text, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery does not reduce the range losslessly.");
        // Reconstructing a deterministic tree is free. The normal cumulative ledger admits each genuinely new child;
        // retained children must remain reusable even when their original holds consumed the available budget.
        return Array.AsReadOnly(children);

        GoogleLongAudioRecoveryPart Child(string value, int start, int end)
        {
            string sourceSha = ArticleAudioManifest.HashText(value);
            byte[] identity = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Version, parent.BindingSha256, start, end, sourceSha }));
            Guid id = new(identity.AsSpan(0, 16));
            string folder = parent.OutputGcsUri[..(parent.OutputGcsUri.LastIndexOf('/') + 1)];
            var request = GoogleLongAudioRequest.Create(id, parent.ProjectNumber, parent.Location, parent.CredentialReferenceId,
                folder + id.ToString("N") + ".wav", value, parent.LanguageCode, parent.VoiceName, "LINEAR16");
            if (id == parent.AttemptId || string.Equals(request.PayloadSha256, parent.PayloadSha256, StringComparison.Ordinal) || request.InputUtf8Bytes >= parent.InputUtf8Bytes)
                throw new InvalidOperationException("Recovery made no progress; the failed request will not be repeated.");
            long cents = checked((long)decimal.Ceiling(value.Length * usdPerMillionCharacters / 10_000m));
            return new(new(start, end, sourceSha, null), request, Math.Max(1, cents));
        }
    }

    private static int NaturalCut(string text)
    {
        var elements = StringInfo.ParseCombiningCharacters(text).ToHashSet(); elements.Add(text.Length);
        int[] letters = new int[text.Length + 1]; int offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int end = offset + rune.Utf16SequenceLength;
            for (int j = offset + 1; j <= end; j++) letters[j] = letters[offset] + (Rune.IsLetterOrDigit(rune) ? 1 : 0);
            offset = end;
        }
        List<(int Cut, int Priority)> candidates = [];
        for (int i = 0; i < text.Length; i++)
        {
            int priority = text[i] is '\n' or '\r' ? 2 : text[i] is '。' or '！' or '？' or '!' or '?'
                || text[i] == '.' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]) ? 1
                : text[i] is '，' or '；' or '：' or '、' or ',' or ';' or ':' ? 2 : -1;
            if (priority < 0) continue;
            int breaks = LogicalLineBreak(i);
            int stop = i + 1;
            while (stop < text.Length && (char.IsWhiteSpace(text[stop]) || text[stop] is '”' or '’' or '」' or '』' or '）' or '》' or '】' or '"' or '\''))
            {
                breaks += LogicalLineBreak(stop);
                stop++;
            }
            i = stop - 1; // Consume the run once, including a trailing run that cannot yield a split.
            if (breaks >= 2) priority = 0; // A blank paragraph separator outranks sentence endings; a single wrapped line does not.
            if (stop >= text.Length || !elements.Contains(stop)
                || letters[stop] == 0 || letters[^1] == letters[stop]) continue;
            candidates.Add((stop, priority));
        }
        if (candidates.Count == 0) throw new InvalidOperationException("No smaller natural source boundary exists; manual text review is required without modifying or resending it.");
        return candidates.OrderBy(x => x.Priority).ThenBy(x => Math.Abs(2L * x.Cut - text.Length)).ThenBy(x => x.Cut).First().Cut;

        int LogicalLineBreak(int at) => text[at] == '\n' || text[at] == '\r' && (at + 1 >= text.Length || text[at + 1] != '\n') ? 1 : 0;
    }
}
