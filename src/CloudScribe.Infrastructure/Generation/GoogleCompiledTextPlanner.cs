using System.Buffers;
using System.Globalization;
using System.Text;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact plain-text byte packing with lossless Unicode coverage. Offline proposals never authorize spending.</summary>
internal static class GoogleCompiledTextPlanner
{
    internal static IReadOnlyList<ChirpNarrationRange> Plan(string source, GoogleSpeechCompilationOptions options,
        IReadOnlyList<ChirpNarrationReuse>? reuse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > ChirpNarrationPlanner.MaximumSourceScalars * 2
            || reuse?.Count > ChirpNarrationPlanner.MaximumCoverageParts)
            throw new InvalidOperationException("Source or accepted coverage exceeds the local planning resource bound.");
        var context = new PlanningContext(source, options);
        int cursor = 0;
        foreach (var retained in (reuse ?? []).OrderBy(x => x.StartScalar))
        {
            if (retained.StartScalar < cursor || retained.EndScalarExclusive <= retained.StartScalar
                || retained.EndScalarExclusive > context.Count || !context.IsElementBoundary(retained.StartScalar)
                || !context.IsElementBoundary(retained.EndScalarExclusive) || !ChirpPolicyValidation.IsHash(retained.ReceiptSha256)
                || !string.Equals(ArticleAudioManifest.HashText(context.Slice(retained.StartScalar, retained.EndScalarExclusive)), retained.TextSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Accepted audio coverage changed, overlaps or splits a text element.");
            context.Run(cursor, retained.StartScalar);
            context.Add(retained.StartScalar, retained.EndScalarExclusive, retained.ReceiptSha256);
            cursor = retained.EndScalarExclusive;
        }
        context.Run(cursor, context.Count);
        return context.Result.AsReadOnly();
    }

    private sealed class PlanningContext
    {
        private readonly string source;
        private readonly GoogleSpeechCompilationOptions options;
        private readonly int[] offsets;
        private readonly HashSet<int> elements;
        internal List<ChirpNarrationRange> Result { get; } = [];
        internal int Count => offsets.Length - 1;

        internal PlanningContext(string value, GoogleSpeechCompilationOptions compilationOptions)
        {
            source = value; options = compilationOptions.Validate();
            List<int> indices = [0]; int offset = 0;
            while (offset < source.Length)
            {
                if (Rune.DecodeFromUtf16(source.AsSpan(offset), out _, out int length) != OperationStatus.Done)
                    throw new InvalidOperationException("Source contains invalid Unicode; no replacement characters were inserted.");
                offset += length; indices.Add(offset);
                if (indices.Count > ChirpNarrationPlanner.MaximumSourceScalars + 1)
                    throw new InvalidOperationException("Source exceeds the local planning resource bound.");
            }
            offsets = indices.ToArray();
            elements = StringInfo.ParseCombiningCharacters(source).ToHashSet(); elements.Add(source.Length);
        }

        internal bool IsElementBoundary(int scalar) => elements.Contains(offsets[scalar]);
        internal string Slice(int start, int end) => source[offsets[start]..offsets[end]];

        internal void Add(int start, int end, string? receipt = null)
        {
            if (Result.Count >= ChirpNarrationPlanner.MaximumCoverageParts)
                throw new InvalidOperationException("The internal narration proposal exceeds its local coverage resource bound.");
            string text = Slice(start, end);
            if (receipt is null && string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("A whitespace-only range cannot fit with speakable text inside the compiled request limit. No text was discarded.");
            Result.Add(new(start, end, ArticleAudioManifest.HashText(text), receipt));
        }

        internal void Run(int start, int end)
        {
            while (start < end)
            {
                // Every scalar needs at least one wire byte. Search the real serialized envelope,
                // including JSON escaping, rather than guessing from characters or input UTF-8.
                int low = start, high = Math.Min(end, start + options.MaximumPayloadBytes);
                while (low < high)
                {
                    int candidate = low + (high - low + 1) / 2;
                    if (GoogleSpeechPlanCompiler.FitsPlainTextPayload(Slice(start, candidate), options)) low = candidate;
                    else high = candidate - 1;
                }
                int stop = low;
                while (stop > start && !IsElementBoundary(stop)) stop--;
                if (stop == start)
                    throw new InvalidOperationException("One text element exceeds the exact compiled request byte limit. No text was changed or discarded.");
                if (stop < end)
                {
                    int natural = stop;
                    while (natural > start && (!IsElementBoundary(natural) || !NaturalEnd(natural))) natural--;
                    if (natural > start) stop = natural;
                    // A long uninterrupted token is split only at a complete Unicode text element.
                }
                Add(start, stop); start = stop;
            }
        }

        private bool NaturalEnd(int end)
        {
            var rune = Rune.GetRuneAt(source, offsets[end - 1]);
            return Rune.IsWhiteSpace(rune) || rune.Value is '!' or '?' or ',' or ';' or ':'
                or 0x3002 or 0xff01 or 0xff1f or 0xff0c or 0xff1b or 0xff1a or 0x3001;
        }
    }
}
