using System.Globalization;
using System.Text;

namespace CloudScribe.Application.Generation;

/// <summary>Pure lossless subdivision at existing clauses. Never admission, retry permission or a provider limit.</summary>
public static class ChirpClauseSubdivision
{
    public const string Version = "chirp3-existing-clauses-v1";

    public static IReadOnlyList<ChirpNarrationRange> Plan(string source, ChirpNarrationRange parent)
    {
        string text = ChirpNarrationPlanner.Slice(source, parent);
        if (parent.ReusedReceiptSha256 is not null)
            throw new InvalidOperationException("Accepted coverage must be reused, never subdivided for generation.");
        var runes = text.EnumerateRunes().ToArray();
        var offsets = new int[runes.Length + 1];
        for (int i = 0; i < runes.Length; i++) offsets[i + 1] = offsets[i] + runes[i].Utf16SequenceLength;
        var elements = StringInfo.ParseCombiningCharacters(text).ToHashSet(); elements.Add(text.Length);
        List<ChirpNarrationRange> ranges = [];
        int start = 0;
        for (int i = 0; i < runes.Length; i++)
        {
            if (!Boundary(runes[i])) continue;
            int end = i + 1;
            while (end < runes.Length && (Boundary(runes[end]) || Closing(runes[end]) || Rune.IsWhiteSpace(runes[end]))) end++;
            if (!elements.Contains(offsets[end]))
                throw new InvalidOperationException("An existing clause boundary splits a text element; source review is required.");
            if (!string.IsNullOrWhiteSpace(text[offsets[start]..offsets[end]]))
            {
                ranges.Add(Range(start, end)); start = end;
            }
            i = end - 1;
        }
        if (start < runes.Length)
        {
            if (string.IsNullOrWhiteSpace(text[offsets[start]..]))
            {
                if (ranges.Count == 0) throw new InvalidOperationException("No nonempty source clause exists.");
                var previous = ranges[^1];
                ranges[^1] = Range(previous.StartScalar - parent.StartScalar, runes.Length);
            }
            else ranges.Add(Range(start, runes.Length));
        }
        if (ranges.Count is < 2 or > 200 || ranges.Any(x => x.EndScalarExclusive - x.StartScalar > ChirpNarrationPlanner.MaximumRequestScalars))
            throw new InvalidOperationException("Existing punctuation does not provide a bounded smaller-request subdivision. No request was created.");
        RequireCoverage(source, parent, text, ranges);
        return ranges.AsReadOnly();

        ChirpNarrationRange Range(int from, int to) => new(parent.StartScalar + from, parent.StartScalar + to,
            ArticleAudioManifest.HashText(text[offsets[from]..offsets[to]]), null);
    }

    private static void RequireCoverage(string source, ChirpNarrationRange parent, string text, List<ChirpNarrationRange> ranges)
    {
        int cursor = parent.StartScalar;
        StringBuilder joined = new();
        foreach (var range in ranges)
        {
            if (range.StartScalar != cursor) throw new InvalidOperationException("Subdivision coverage is inconsistent.");
            joined.Append(ChirpNarrationPlanner.Slice(source, range)); cursor = range.EndScalarExclusive;
        }
        if (cursor != parent.EndScalarExclusive || !string.Equals(joined.ToString(), text, StringComparison.Ordinal))
            throw new InvalidOperationException("Subdivision does not preserve the exact original text.");
    }

    private static bool Boundary(Rune value) => value.Value is '\n' or ',' or ';' or ':' or '!' or '?'
        or 0x3002 or 0xff01 or 0xff1f or 0xff0c or 0xff1b or 0xff1a or 0x3001;
    private static bool Closing(Rune value) => value.Value is 0x201d or 0x2019 or 0x300d or 0x300f
        or 0xff09 or 0x300b or 0x3011 or '"' or '\'';
}
