using System.Security.Cryptography;

namespace CloudScribe.Infrastructure.Generation;

internal static class PrivateRecordProtection
{
    internal const int MaximumPlaintextBytes = 32768;
    internal const int MaximumProtectedBytes = 65536;
    private static readonly byte[] Purpose = "CloudScribe.PrivateResponseEnvelope.v1"u8.ToArray();

    internal static byte[] Protect(byte[] bytes)
    {
        if (bytes.Length > MaximumPlaintextBytes) throw new InvalidDataException("Private envelope exceeds its plaintext bound.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Current-user private protection requires Windows.");
        var result = ProtectedData.Protect(bytes, Purpose, DataProtectionScope.CurrentUser);
        if (result.Length > MaximumProtectedBytes) throw new InvalidDataException("Private envelope exceeds its protected bound.");
        return result;
    }

    internal static byte[] Unprotect(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumProtectedBytes) throw new InvalidDataException("Protected envelope exceeds its bound.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Current-user private protection requires Windows.");
        var result = ProtectedData.Unprotect(bytes, Purpose, DataProtectionScope.CurrentUser);
        if (result.Length > MaximumPlaintextBytes) throw new InvalidDataException("Private envelope exceeds its plaintext bound.");
        return result;
    }
}
