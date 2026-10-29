using System.Buffers;
using System.Globalization;
using System.Text;

namespace CloudScribe.Application.Generation;

/// <summary>Offline proposals only. No result of this planner authorizes a provider request.</summary>
public static class ChirpNarrationPlanner
{
    public const string Version = "chirp3-existing-boundaries-v1";
    // A local quality/cost heuristic, NOT a documented Google limit or acceptance guarantee.
    public const int MaximumRequestScalars = 333;
    public const int MaximumSourceScalars = 1_000_000;
    public const int MaximumCoverageParts = 10_000;

    public static IReadOnlyList<ChirpNarrationRange> Plan(string source,
        IReadOnlyList<ChirpNarrationReuse>? reuse = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (reuse?.Count > MaximumCoverageParts)
            throw new InvalidOperationException("Accepted audio coverage exceeds the local planning bound.");
        int[] offsets = ScalarOffsets(source);
        int count = offsets.Length - 1;
        var elements = StringInfo.ParseCombiningCharacters(source).ToHashSet();
        elements.Add(source.Length);
        ChirpNarrationReuse[] retained = (reuse ?? []).OrderBy(x => x.StartScalar).ToArray();
        List<ChirpNarrationRange> result = [];
        int cursor = 0;
        foreach (var interval in retained)
        {
            if (interval.StartScalar < cursor || interval.EndScalarExclusive <= interval.StartScalar
                || interval.EndScalarExclusive > count
                || !elements.Contains(offsets[interval.StartScalar]) || !elements.Contains(offsets[interval.EndScalarExclusive]))
                throw new InvalidOperationException("Accepted audio coverage is overlapping, outside the source or splits a text element.");
            string text = Slice(source, offsets, interval.StartScalar, interval.EndScalarExclusive);
            if (!string.Equals(ArticleAudioManifest.HashText(text), interval.TextSha256, StringComparison.Ordinal)
                || !IsHash(interval.ReceiptSha256))
                throw new InvalidOperationException("Accepted audio coverage does not match the exact source or receipt hash.");
            AppendRun(source, offsets, elements, result, cursor, interval.StartScalar);
            if (result.Count >= MaximumCoverageParts)
                throw new InvalidOperationException("The internal narration proposal has too many coverage parts.");
            result.Add(new(interval.StartScalar, interval.EndScalarExclusive, interval.TextSha256, interval.ReceiptSha256));
            cursor = interval.EndScalarExclusive;
        }
        AppendRun(source, offsets, elements, result, cursor, count);
        if (result.Count > MaximumCoverageParts)
            throw new InvalidOperationException("The internal narration proposal has too many coverage parts.");
        return result.AsReadOnly();
    }

    private static void AppendRun(string source, int[] offsets, HashSet<int> elements, List<ChirpNarrationRange> result, int start, int end)
    {
        while (start < end)
        {
            if (result.Count >= MaximumCoverageParts)
                throw new InvalidOperationException("The internal narration proposal has too many coverage parts.");
            int stop = Math.Min(start + MaximumRequestScalars, end);
            if (stop < end)
            {
                int floor = start + (MaximumRequestScalars * 9 + 9) / 10;
                int candidate = LastBoundary(source, offsets, elements, floor - 1, stop, punctuationOnly: true);
                if (candidate < 0) candidate = LastBoundary(source, offsets, elements, floor - 1, stop, punctuationOnly: false);
                if (candidate < 0) candidate = LastBoundary(source, offsets, elements, start, stop, punctuationOnly: false);
                if (candidate < 0)
                    throw new InvalidOperationException("No existing sentence, clause or whitespace boundary fits the local Chirp 3 planning window. Review the text; no request was created.");
                stop = candidate;
                while (stop < end && stop - start < MaximumRequestScalars && IsClosing(source, offsets[stop])) stop++;
                // Never divide an emoji sequence, combining accent, CRLF or another grapheme.
                if (!elements.Contains(offsets[stop]))
                    throw new InvalidOperationException("An existing boundary would split a text element; review the source before planning.");
            }
            string text = Slice(source, offsets, start, stop);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("A proposed request contains only whitespace; review the source without discarding it.");
            result.Add(new(start, stop, ArticleAudioManifest.HashText(text), null));
            start = stop;
        }
    }

    private static int LastBoundary(string source, int[] offsets, HashSet<int> elements, int start, int end, bool punctuationOnly)
    {
        for (int i = end - 1; i >= start; i--)
        {
            Rune scalar = Rune.GetRuneAt(source, offsets[i]);
            // ASCII '.' is deliberately ambiguous (decimals/abbreviations); the reviewed
            // heuristic uses explicit CJK full stops and other existing clause boundaries.
            bool punctuation = scalar.IsAscii ? scalar.Value is '!' or '?' or '\n' or ',' or ';' or ':'
                : scalar.Value is 0x3002 or 0xff01 or 0xff1f or 0xff0c or 0xff1b or 0xff1a or 0x3001;
            if ((punctuation || !punctuationOnly && Rune.IsWhiteSpace(scalar)) && elements.Contains(offsets[i + 1])) return i + 1;
        }
        return -1;
    }

    public static string Slice(string source, ChirpNarrationRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        int[] offsets = ScalarOffsets(source);
        if (range.StartScalar < 0 || range.EndScalarExclusive <= range.StartScalar || range.EndScalarExclusive >= offsets.Length)
            throw new InvalidOperationException("The proposed coverage is outside the source.");
        string text = Slice(source, offsets, range.StartScalar, range.EndScalarExclusive);
        if (!string.Equals(ArticleAudioManifest.HashText(text), range.TextSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The source no longer matches the proposed coverage.");
        return text;
    }

    /// <summary>Materializes a complete proposed coverage in one indexed pass; still grants no authority.</summary>
    public static IReadOnlyList<string> SliceAll(string source, IReadOnlyList<ChirpNarrationRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count is < 1 or > MaximumCoverageParts)
            throw new InvalidOperationException("Proposed narration coverage is empty or exceeds the local bound.");
        int[] offsets = ScalarOffsets(source);
        var elements = StringInfo.ParseCombiningCharacters(source).ToHashSet();
        elements.Add(source.Length);
        List<string> result = [];
        int cursor = 0;
        foreach (var range in ranges)
        {
            if (range.StartScalar != cursor || range.EndScalarExclusive <= cursor || range.EndScalarExclusive >= offsets.Length
                || !elements.Contains(offsets[range.StartScalar]) || !elements.Contains(offsets[range.EndScalarExclusive]))
                throw new InvalidOperationException("Proposed narration coverage is missing, overlapping, unordered or splits a text element.");
            string text = Slice(source, offsets, range.StartScalar, range.EndScalarExclusive);
            if (!string.Equals(ArticleAudioManifest.HashText(text), range.TextSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("The source no longer matches proposed narration coverage.");
            result.Add(text);
            cursor = range.EndScalarExclusive;
        }
        if (cursor != offsets.Length - 1)
            throw new InvalidOperationException("Proposed narration coverage does not reach the original article cutoff.");
        return result.AsReadOnly();
    }

    private static int[] ScalarOffsets(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumSourceScalars * 2) throw new InvalidOperationException("Source exceeds the local planning bound.");
        List<int> offsets = [0];
        int offset = 0;
        while (offset < source.Length)
        {
            if (Rune.DecodeFromUtf16(source.AsSpan(offset), out _, out int length) != OperationStatus.Done)
                throw new InvalidOperationException("Source contains invalid Unicode; no replacement characters were inserted.");
            offset += length;
            offsets.Add(offset);
            if (offsets.Count > MaximumSourceScalars + 1) throw new InvalidOperationException("Source exceeds the local planning bound.");
        }
        return offsets.ToArray();
    }

    private static string Slice(string source, int[] offsets, int start, int end) => source[offsets[start]..offsets[end]];
    private static bool IsClosing(string source, int offset) => Rune.GetRuneAt(source, offset).Value
        is 0x201d or 0x2019 or 0x300d or 0x300f or 0xff09 or 0x300b or 0x3011 or '"' or '\'';
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
}
