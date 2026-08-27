namespace CloudScribe.Domain.Safety;

public sealed record RestoreManifestFileBinding(string RelativePath, long LengthBytes, string Sha256Hex)
{
    public RestoreManifestFileBinding Validate()
    {
        ValidateBinding(RelativePath, LengthBytes, Sha256Hex);
        return this;
    }

    private static void ValidateBinding(string relativePath, long lengthBytes, string sha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Restore manifest path is not a safe relative path.");
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new InvalidOperationException("Restore manifest path contains an unsafe traversal segment.");
        if (lengthBytes < 0) throw new ArgumentOutOfRangeException(nameof(lengthBytes));
        if (sha256Hex.Length != 64 || sha256Hex.Any(static c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Restore manifest SHA-256 must be 64 hexadecimal characters.");
    }
}
