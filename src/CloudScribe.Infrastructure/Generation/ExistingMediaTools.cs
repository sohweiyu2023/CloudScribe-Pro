namespace CloudScribe.Infrastructure.Generation;

/// <summary>Discovers existing tools only. Never downloads, installs or executes a candidate.</summary>
public static class ExistingMediaTools
{
    public static string? Find(IEnumerable<string?> directories)
    {
        foreach (string? directory in directories.Take(128))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)
                || directory.StartsWith(@"\\", StringComparison.Ordinal)
                || directory.StartsWith("//", StringComparison.Ordinal)) continue;
            try
            {
                string local = Path.GetFullPath(directory);
                if (local.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                var drive = new DriveInfo(Path.GetPathRoot(local)!);
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (HasLinkedDirectory(local)) continue;
                string encoder = Path.Combine(local, "ffmpeg.exe");
                string probe = Path.Combine(local, "ffprobe.exe");
                if (IsRegularFile(encoder) && IsRegularFile(probe)) return encoder;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { /* Inaccessible discovery candidates are unusable; the UI offers an explicit picker. */ }
        }
        return null;
    }

    private static bool HasLinkedDirectory(string path)
    {
        var ancestors = new Stack<DirectoryInfo>();
        for (DirectoryInfo? item = new(path); item is not null; item = item.Parent) ancestors.Push(item);
        foreach (var item in ancestors)
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }

    private static bool IsRegularFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        return !attributes.HasFlag(FileAttributes.ReparsePoint) && !attributes.HasFlag(FileAttributes.Directory);
    }
}
