using System.Text;
using CloudReader.Core.Interfaces;
using CloudReader.Core.Models;

namespace CloudReader.Core.Processing;

public sealed class Utf8Chunker : IUtf8Chunker
{
    private static readonly HashSet<System.Text.Rune> SentenceBreaks =
    [new('.'), new('?'), new('!'), new(';'), new(':'), new('。'), new('？'), new('！'), new('；'), new('：')];

    public IReadOnlyList<ChunkResult> Chunk(string text, int maxChunkBytes = 4500)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var runes = text.EnumerateRunes().ToArray();
        var chunks = new List<ChunkResult>();
        var start = 0;
        var idx = 0;

        while (start < runes.Length)
        {
            var end = FindSafeEnd(runes, start, maxChunkBytes);
            var segment = string.Concat(runes[start..end].Select(static r => r.ToString()));
            chunks.Add(new ChunkResult(idx++, segment, Encoding.UTF8.GetByteCount(segment), segment.Length));
            start = end;
        }

        return chunks;
    }

    private static int FindSafeEnd(System.Text.Rune[] runes, int start, int maxChunkBytes)
    {
        var bytes = 0;
        var bestBoundary = -1;
        var i = start;

        while (i < runes.Length)
        {
            var current = runes[i].ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(current);
            if (bytes + runeBytes > maxChunkBytes) break;

            bytes += runeBytes;
            if (SentenceBreaks.Contains(runes[i]) || System.Text.Rune.IsWhiteSpace(runes[i])) bestBoundary = i + 1;
            i++;
        }

        if (i == runes.Length) return i;
        if (bestBoundary > start) return bestBoundary;
        return Math.Max(start + 1, i);
    }
}
