using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Infrastructure.Generation;

public static class GoogleVoiceCatalogIdentity
{
    public static string CreateFingerprint(
        string provenanceId,
        GoogleVoiceCatalogEntry voice)
    {
        string canonical = string.Join("\n",
            provenanceId,
            voice.Name,
            string.Join(",", voice.LanguageCodes.Order(StringComparer.OrdinalIgnoreCase)),
            voice.SsmlGender,
            voice.NaturalSampleRateHertz.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"google-voice:sha256:{hash}";
    }
}
