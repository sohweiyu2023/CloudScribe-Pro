using System.Security.Cryptography;

namespace CloudScribe.Application.Generation;

public sealed class GenerationReleaseVerifier
{
    private readonly long _maximumOutputBytes;

    public GenerationReleaseVerifier(long maximumOutputBytes = 2L * 1024 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputBytes);
        _maximumOutputBytes = maximumOutputBytes;
    }

    public GenerationReleaseVerificationResult Verify(GenerationReleaseReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (!receipt.Verify())
            return new(false, "receipt-integrity-failed", null);

        string fullPath;
        try { fullPath = Path.GetFullPath(receipt.OutputPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(false, "output-path-invalid", null);
        }

        if (!Path.IsPathFullyQualified(fullPath))
            return new(false, "output-path-not-absolute", null);

        var file = new FileInfo(fullPath);
        if (!file.Exists)
            return new(false, "output-missing", null);
        if (file.Length <= 0 || file.Length > _maximumOutputBytes)
            return new(false, "output-size-out-of-bounds", null);

        string observed;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            observed = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return new(false, "output-read-failed", null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, "output-read-failed", null);
        }

        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(receipt.OutputSha256),
            Convert.FromHexString(observed)))
        {
            return new(false, "output-hash-mismatch", observed);
        }

        return new(true, "release-verified", observed);
    }
}
