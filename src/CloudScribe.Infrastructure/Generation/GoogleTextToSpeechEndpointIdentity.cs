using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Derives a non-secret stable identity from the exact admitted synthesis URI. The full URI remains
/// protected with credential metadata; this hash lets persisted account/project evidence detect an
/// endpoint-path change without hard-coding or exposing the endpoint itself.
/// </summary>
public static class GoogleTextToSpeechEndpointIdentity
{
    private const string Prefix = "tts-synthesis-";

    public static string Create(Uri synthesisEndpoint)
    {
        ArgumentNullException.ThrowIfNull(synthesisEndpoint);
        if (!synthesisEndpoint.IsAbsoluteUri
            || !string.Equals(synthesisEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !synthesisEndpoint.IsDefaultPort
            || !string.IsNullOrEmpty(synthesisEndpoint.UserInfo)
            || !string.IsNullOrEmpty(synthesisEndpoint.Query)
            || !string.IsNullOrEmpty(synthesisEndpoint.Fragment)
            || string.IsNullOrWhiteSpace(synthesisEndpoint.AbsolutePath)
            || string.Equals(synthesisEndpoint.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Google synthesis endpoint identity requires an explicit credential-free HTTPS API path.",
                nameof(synthesisEndpoint));
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(synthesisEndpoint.AbsoluteUri));
        // 48 hex characters plus the prefix stays inside the provider stable-ID 64-character limit.
        return Prefix + Convert.ToHexString(hash.AsSpan(0, 24)).ToLowerInvariant();
    }
}
