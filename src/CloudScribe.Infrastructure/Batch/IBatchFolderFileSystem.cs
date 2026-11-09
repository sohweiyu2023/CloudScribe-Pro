namespace CloudScribe.Infrastructure.Batch;

/// <summary>Read-only filesystem seam for deterministic traversal, mutation and failure tests.</summary>
internal interface IBatchFolderFileSystem
{
    FileAttributes GetAttributes(string path);
    string[] GetEntries(string folder);
    uint GetDirectoryReparseTag(string folder);
}
