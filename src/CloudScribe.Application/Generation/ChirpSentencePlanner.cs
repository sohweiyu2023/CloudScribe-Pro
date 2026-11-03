using System.Globalization;
using System.Text;

namespace CloudScribe.Application.Generation;

/// <summary>Whole existing sentence groups; local bounds are not provider acceptance guarantees.</summary>
public static class ChirpSentencePlanner
{
    public const string Version = "chirp3-existing-sentence-groups-v1";

    public static IReadOnlyList<ChirpNarrationRange> Plan(string source, IReadOnlyList<ChirpNarrationReuse> reuse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source); ArgumentNullException.ThrowIfNull(reuse);
        if (source.Length > ChirpNarrationPlanner.MaximumSourceScalars * 2 || reuse.Count > ChirpNarrationPlanner.MaximumCoverageParts)
            throw new InvalidOperationException("Sentence planning exceeds the local source or reuse bound.");
        var context = new PlanningContext(source); int cursor = 0;
        foreach (var retained in reuse.OrderBy(x => x.StartScalar))
        {
            if (retained.StartScalar < cursor || retained.EndScalarExclusive <= retained.StartScalar || retained.EndScalarExclusive > context.Count
                || retained.ReceiptSha256 is not { Length: 64 } || retained.ReceiptSha256.Any(x => x is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || !string.Equals(ArticleAudioManifest.HashText(context.Slice(retained.StartScalar, retained.EndScalarExclusive)), retained.TextSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Accepted sentence-plan coverage changed or overlaps.");
            context.Run(cursor, retained.StartScalar); context.Add(retained.StartScalar, retained.EndScalarExclusive, retained.ReceiptSha256); cursor = retained.EndScalarExclusive;
        }
        context.Run(cursor, context.Count);
        _ = ChirpNarrationPlanner.SliceAll(source, context.Result);
        return context.Result.AsReadOnly();
    }

    private sealed class PlanningContext
    {
        private readonly string source;
        private readonly Rune[] runes;
        private readonly int[] offsets;
        private readonly HashSet<int> elements;
        internal List<ChirpNarrationRange> Result { get; } = [];
        internal int Count => runes.Length;

        internal PlanningContext(string value)
        {
            source = value; runes = source.EnumerateRunes().ToArray();
            if (runes.Length > ChirpNarrationPlanner.MaximumSourceScalars) throw new InvalidOperationException("Source exceeds the local planning bound.");
            offsets = new int[runes.Length + 1];
            for (int i = 0; i < runes.Length; i++) offsets[i + 1] = offsets[i] + runes[i].Utf16SequenceLength;
            elements = StringInfo.ParseCombiningCharacters(source).ToHashSet(); elements.Add(source.Length);
        }

        internal string Slice(int start, int end) => source[offsets[start]..offsets[end]];
        internal void Add(int start, int end, string? receipt = null)
        {
            if (Result.Count >= ChirpNarrationPlanner.MaximumCoverageParts || start >= end
                || !elements.Contains(offsets[start]) || !elements.Contains(offsets[end]))
                throw new InvalidOperationException("Sentence planning would split a text element or exceed its part bound.");
            Result.Add(new(start, end, ArticleAudioManifest.HashText(Slice(start, end)), receipt));
        }

        private List<(int Start, int End)> Units(int start, int end)
        {
            List<(int Start, int End)> units = []; int left = start, i = start;
            while (i < end)
            {
                if (!Strong(runes[i])) { i++; continue; }
                int stop = i + 1;
                while (stop < end && (Strong(runes[stop]) || Closing(runes[stop]) || Rune.IsWhiteSpace(runes[stop]))) stop++;
                if (runes[left..stop].Any(Rune.IsLetterOrDigit)) { units.Add((left, stop)); left = stop; }
                i = stop;
            }
            if (left < end)
            {
                if (runes[left..end].Any(Rune.IsLetterOrDigit)) units.Add((left, end));
                else if (units.Count > 0) units[^1] = (units[^1].Start, end);
                else throw new InvalidOperationException("The missing range has no speakable sentence unit.");
            }
            return units;
        }

        internal void Run(int start, int end)
        {
            if (start == end) return;
            var units = Units(start, end); int groupStart = start, groupEnd = start;
            foreach (var unit in units)
            {
                if (unit.End - unit.Start > ChirpNarrationPlanner.MaximumRequestScalars)
                    throw new InvalidOperationException("An existing sentence exceeds the local planning window; no automatic clause fallback was authorized.");
                if (groupEnd > groupStart && (unit.End - groupStart > ChirpNarrationPlanner.MaximumRequestScalars
                    || start > 0 && groupStart == start && groupEnd == units[0].End))
                { Add(groupStart, groupEnd); groupStart = unit.Start; }
                groupEnd = unit.End;
            }
            Add(groupStart, groupEnd);
        }
    }

    private static bool Strong(Rune r) => r.Value is '!' or '?' or 0x3002 or 0xff01 or 0xff1f;
    private static bool Closing(Rune r) => r.Value is '"' or '\'' or 0x201d or 0x2019 or 0x300d or 0x300f or 0xff09 or 0x300b or 0x3011;
}
