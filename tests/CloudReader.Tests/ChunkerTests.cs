using System.Text;
using CloudReader.Core.Processing;

namespace CloudReader.Tests;

public sealed class ChunkerTests
{
    [Fact]
    public void Chunk_MixedEnglishCjk_RespectsByteLimit()
    {
        var chunker = new Utf8Chunker();
        var input = string.Concat(Enumerable.Repeat("Hello 世界。", 1000));
        var chunks = chunker.Chunk(input, 450);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.True(Encoding.UTF8.GetByteCount(chunk.Text) <= 450));
    }

    [Fact]
    public void Chunk_DoesNotBreakUtf8Runes()
    {
        var chunker = new Utf8Chunker();
        var text = "😀😀😀😀😀😀😀😀😀😀";
        var chunks = chunker.Chunk(text, 9);

        Assert.All(chunks, chunk => Assert.DoesNotContain('\uFFFD', chunk.Text));
    }
}
