using System.Security.Cryptography;

namespace CloudScribe.Domain.Safety;

public static class RestoreManifestContentBinding
{
    public static void Verify(string stagingRoot, IReadOnlyList<RestoreManifestFileBinding> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(bindings);
        var root = Path.GetFullPath(stagingRoot);
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Restore staging root cannot be a reparse point.");
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in bindings)
        {
            binding.Validate();
            var normalizedRelativePath = string.Join(Path.DirectorySeparatorChar, binding.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
            if (!seen.Add(normalizedRelativePath))
                throw new InvalidDataException("Restore manifest contains duplicate or case-colliding file paths.");
            var path = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Restore manifest file escapes the staging root.");
            if (!File.Exists(path))
                throw new InvalidOperationException("Restore manifest file is missing from staging.");
            EnsureNoReparsePoints(root, path);
            var info = new FileInfo(path);
            if (info.Length != binding.LengthBytes)
                throw new InvalidDataException("Restore manifest file length does not match staged content.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
            var observed = SHA256.HashData(stream);
            var expected = Convert.FromHexString(binding.Sha256Hex);
            if (!CryptographicOperations.FixedTimeEquals(observed, expected))
                throw new InvalidDataException("Restore manifest SHA-256 does not match staged content.");
        }
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Restore manifest content cannot traverse a symbolic link or reparse point.");
        }
    }
}
