using System.Security.Cryptography;

namespace CloudScribe.Domain.Safety;

public sealed record RestoreManifestFileBinding(string RelativePath, long LengthBytes, string Sha256Hex)
{
    public RestoreManifestFileBinding Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RelativePath);
        if (Path.IsPathRooted(RelativePath) || RelativePath.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Restore manifest path is not a safe relative path.");
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

        foreach (var binding in bindings)
        {
            binding.Validate();
            var path = Path.GetFullPath(Path.Combine(root, binding.RelativePath));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Restore manifest file escapes the staging root.");
            if (!File.Exists(path))
                throw new InvalidOperationException("Restore manifest file is missing from staging.");
            var info = new FileInfo(path);
            if (info.Length != binding.LengthBytes)
                throw new InvalidDataException("Restore manifest file length does not match staged content.");
            var observed = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(observed), Convert.FromHexString(binding.Sha256Hex)))
                throw new InvalidDataException("Restore manifest SHA-256 does not match staged content.");
        }
    }
}
