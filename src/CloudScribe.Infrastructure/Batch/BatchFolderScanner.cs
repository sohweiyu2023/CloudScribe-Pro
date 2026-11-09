using CloudScribe.Application.Batch;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Finds TXT files while skipping detected junctions, directory symbolic links and unknown reparse points.
/// Known Windows Cloud Files placeholders are ordinary input folders, not links. Unreadable subfolders are skipped.
/// There is no depth or file-count limit; traversal is iterative. Path checks and enumeration are not atomic against
/// concurrent filesystem changes; queued folders are rechecked to narrow, not eliminate, that race.</summary>
public static class BatchFolderScanner
{
    public static BatchFolderScan Scan(string root, bool recursive, CancellationToken cancellationToken = default) =>
        Scan(root, recursive, PhysicalBatchFolderFileSystem.Instance, cancellationToken);

    internal static BatchFolderScan Scan(string root, bool recursive, IBatchFolderFileSystem fileSystem, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(fileSystem);
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var files = new List<string>();
        int links = 0, unreadable = 0;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = pending.Pop();
            if (!visited.Add(Path.TrimEndingDirectorySeparator(folder))) continue;
            string[] entries;
            try
            {
                // Recheck on every pop, including the root: a queued physical/cloud folder may have become a link.
                if (!CanEnter(folder, fileSystem.GetAttributes(folder), fileSystem, cancellationToken)) { links++; continue; }
                entries = fileSystem.GetEntries(folder);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception error) when (IsUnreadable(error))
            {
                unreadable++;
                continue;
            }
            var collected = CollectEntries(entries, recursive, fileSystem, files, cancellationToken);
            links += collected.Links;
            unreadable += collected.Unreadable;
            var children = collected.Children;
            // Pushed in reverse natural order so folders are visited in natural order.
            children.Sort(BatchNaturalOrder.Instance);
            for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        files.Sort(BatchNaturalOrder.Instance);
        return new(files, links, unreadable);
    }

    private static (List<string> Children, int Links, int Unreadable) CollectEntries(string[] entries, bool recursive,
        IBatchFolderFileSystem fileSystem, List<string> files, CancellationToken cancellationToken)
    {
        int links = 0, unreadable = 0;
        var children = new List<string>();
        foreach (string entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try { attributes = fileSystem.GetAttributes(entry); }
            catch (Exception error) when (IsUnreadable(error))
            {
                continue; // Vanished or unreadable entry: nothing to add.
            }
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (!recursive) continue;
                try
                {
                    if (!CanEnter(entry, attributes, fileSystem, cancellationToken)) { links++; continue; }
                }
                catch (Exception error) when (IsUnreadable(error))
                {
                    unreadable++;
                    continue;
                }
                children.Add(entry);
            }
            else if (entry.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) files.Add(entry);
        }
        return (children, links, unreadable);
    }
    private static bool CanEnter(string path, FileAttributes attributes, IBatchFolderFileSystem fileSystem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!attributes.HasFlag(FileAttributes.Directory)) throw new IOException("The input folder is no longer a directory.");
        if (!attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        uint tag = fileSystem.GetDirectoryReparseTag(path);
        cancellationToken.ThrowIfCancellationRequested();
        return IsCloudDirectoryTag(tag);
    }
    // Only the sixteen documented Cloud Files tags. The mask ignores exactly their variant nibble, not
    // unknown Microsoft tags or the name-surrogate bit used by junctions/symlinks. Do not use LinkTarget == null.
    // https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/c8e77b37-3909-4fe6-a4ea-2b9d423b1ee4
    internal static bool IsCloudDirectoryTag(uint tag) => (tag & 0xFFFF0FFFu) == 0x9000001Au;

    private static bool IsUnreadable(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
