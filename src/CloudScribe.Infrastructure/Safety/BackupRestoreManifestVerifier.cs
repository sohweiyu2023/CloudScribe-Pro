using System.Security.Cryptography;

namespace CloudScribe.Infrastructure.Safety;

public static class BackupRestoreManifestVerifier
{
    public static bool VerifyEcdsaSha256(
        ReadOnlySpan<byte> canonicalManifestBytes,
        ReadOnlySpan<byte> signatureDer,
        string trustedPublicKeyPem,
        int maximumManifestBytes = 4 * 1024 * 1024,
        int maximumSignatureBytes = 1024)
    {
        if (canonicalManifestBytes.IsEmpty)
            throw new ArgumentException("Backup manifest bytes are required.", nameof(canonicalManifestBytes));
        if (canonicalManifestBytes.Length > maximumManifestBytes)
            throw new InvalidDataException("Backup manifest exceeds the bounded verification size.");
        if (signatureDer.IsEmpty || signatureDer.Length > maximumSignatureBytes)
            throw new InvalidDataException("Backup manifest signature size is invalid.");
        if (string.IsNullOrWhiteSpace(trustedPublicKeyPem))
            throw new ArgumentException("A trusted backup-signing public key is required.", nameof(trustedPublicKeyPem));
        if (maximumManifestBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumManifestBytes));
        if (maximumSignatureBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSignatureBytes));

        using var verifier = ECDsa.Create();
        try
        {
            verifier.ImportFromPem(trustedPublicKeyPem);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Trusted backup-signing public key is not a valid ECDSA public key.", exception);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Trusted backup-signing public key could not be imported.", exception);
        }

        return verifier.VerifyData(
            canonicalManifestBytes,
            signatureDer,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
    }

    public static void RequireAuthenticated(
        ReadOnlySpan<byte> canonicalManifestBytes,
        ReadOnlySpan<byte> signatureDer,
        string trustedPublicKeyPem)
    {
        if (!VerifyEcdsaSha256(canonicalManifestBytes, signatureDer, trustedPublicKeyPem))
            throw new InvalidDataException("Backup manifest signature is not valid for the trusted signing key.");
    }
}
