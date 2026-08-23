using System.Security.Cryptography;

namespace CloudScribe.Domain.Safety;

public sealed record RestoreManifestFileBinding(string RelativePath, long LengthBytes, string Sha256Hex)
{
    public RestoreManifestFileBinding Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RelativePath);
        if (Path.IsPathRooted(RelativePath))
            throw new InvalidOperationException("Restore manifest path is not a safe relative path.");
        var segments = RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new InvalidOperationException("Restore manifest path contains an unsafe traversal segment.");
        if (LengthBytes < 0) throw new ArgumentOutOfRangeException(nameof(LengthBytes));
        if (Sha256Hex.Length != 64 || Sha256Hex.Any(static c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Restore manifest SHA-256 must be 64 hexadecimal characters.");
        return this;
    }
}

public static class RestoreManifestContentBinding
{
    public static void Verify(string stagingRoot, IReadOnlyList<RestoreManifestFileBinding> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(bindings);
        var root = Path.GetFullPath(stagingRoot);
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
}
