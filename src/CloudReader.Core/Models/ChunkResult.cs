namespace CloudReader.Core.Models;

public sealed record ChunkResult(int Index, string Text, int Utf8Bytes, int CharacterCount);
