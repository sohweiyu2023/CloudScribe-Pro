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
