namespace CloudScribe.Application.Batch;

/// <summary>What a folder scan found: TXT files in natural folder-then-name order, plus what it could not or would not
/// enter, so the window can say so instead of failing the whole add.</summary>
public sealed record BatchFolderScan(IReadOnlyList<string> Files, int SkippedLinks, int UnreadableFolders);
