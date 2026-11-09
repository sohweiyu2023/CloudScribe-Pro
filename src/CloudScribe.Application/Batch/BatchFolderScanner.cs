namespace CloudScribe.Application.Batch;

/// <summary>Finds the TXT files in a local folder (and optionally its subfolders) without following junctions or
/// symbolic links to folders, so a link that points back to an ancestor can never loop forever, and without failing
/// the whole scan when one subfolder cannot be read (for example a protected system folder on a dropped drive). There is
/// no limit on depth or file count; the scan is iterative, so deep trees cannot exhaust the stack.</summary>
public static class BatchFolderScanner
{
    public static BatchFolderScan Scan(string root, bool recursive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer);
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
            try { entries = Directory.GetFileSystemEntries(folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unreadable++;
                continue;
            }
            var children = new List<string>();
            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue; // Vanished or unreadable entry: nothing to add.
                }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!recursive) continue;
                    // Junctions and directory symbolic links are not followed: they may point back to an ancestor or
                    // elsewhere on the disk. The user can drop the link's target directly if it is wanted.
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) { links++; continue; }
                    children.Add(entry);
                }
                else if (entry.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) files.Add(entry);
            }
            // Pushed in reverse natural order so folders are visited in natural order.
            children.Sort(BatchNaturalOrder.Instance);
            for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
        }
        files.Sort(BatchNaturalOrder.Instance);
        return new(files, links, unreadable);
    }
}
