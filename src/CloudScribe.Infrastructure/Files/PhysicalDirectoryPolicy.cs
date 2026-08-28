namespace CloudScribe.Infrastructure.Files;

/// <summary>
/// Creates a directory only after validating that every existing path component is a physical
/// directory rather than a symbolic link or reparse point. This is a best-effort managed guard;
/// callers still use create-new file semantics so a later path swap cannot overwrite an existing file.
/// </summary>
public static class PhysicalDirectoryPolicy
{
    public static string ValidateExistsWithoutLinks(string path, string rejectionMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectionMessage);

        string fullPath = Path.GetFullPath(path);
        DirectoryInfo directory = new(fullPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Directory '{fullPath}' does not exist.");
        }

        ValidateExistingAncestors(directory, rejectionMessage);
        return fullPath;
    }

    public static string EnsureExistsWithoutLinks(string path, string rejectionMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectionMessage);

        string fullPath = Path.GetFullPath(path);
        DirectoryInfo target = new(fullPath);
        Stack<DirectoryInfo> missingDirectories = new();
        DirectoryInfo? current = target;

        while (current is not null && !current.Exists)
        {
            ThrowIfLinkOrReparsePoint(current, rejectionMessage);
            missingDirectories.Push(current);
            current = current.Parent;
        }

        if (current is null)
        {
            throw new DirectoryNotFoundException($"No existing parent directory was found for '{fullPath}'.");
        }

        ValidateExistingAncestors(current, rejectionMessage);

        while (missingDirectories.Count > 0)
        {
            DirectoryInfo directory = missingDirectories.Pop();
            directory.Create();
            directory.Refresh();
            if (!directory.Exists)
            {
                throw new IOException($"Directory creation did not produce a physical directory at '{directory.FullName}'.");
            }

            ThrowIfLinkOrReparsePoint(directory, rejectionMessage);
        }

        // Recheck after creation to narrow the path-swap window before the caller opens its files.
        ValidateExistingAncestors(new DirectoryInfo(fullPath), rejectionMessage);
        return fullPath;
    }

    private static void ValidateExistingAncestors(DirectoryInfo directory, string rejectionMessage)
    {
        DirectoryInfo? current = directory;
        while (current is not null)
        {
            if (!current.Exists)
            {
                throw new DirectoryNotFoundException($"Directory '{current.FullName}' disappeared during validation.");
            }

            ThrowIfLinkOrReparsePoint(current, rejectionMessage);
            current = current.Parent;
        }
    }

    private static void ThrowIfLinkOrReparsePoint(DirectoryInfo directory, string rejectionMessage)
    {
        directory.Refresh();
        if (directory.LinkTarget is not null
            || (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidOperationException(rejectionMessage);
        }
    }
}
