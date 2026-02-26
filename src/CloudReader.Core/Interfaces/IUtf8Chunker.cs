using CloudReader.Core.Models;

namespace CloudReader.Core.Interfaces;

public interface IUtf8Chunker
{
    IReadOnlyList<ChunkResult> Chunk(string text, int maxChunkBytes = 4500);
}
