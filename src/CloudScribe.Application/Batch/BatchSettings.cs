using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Application.Batch;

/// <summary>Settings chosen once for the whole batch. Their hash is pinned in the queue so a changed voice,
/// language or engine cannot silently apply to files that were reviewed with different settings.</summary>
public sealed record BatchSettings(
    string LanguageCode,
    string VoiceStableId,
    string VoiceLabel,
    string Engine,
    BatchFailurePolicy FailurePolicy,
    bool SkipUrls = false,
    string? UrlTransformVersion = null)
{
    public string Sha256()
    {
        string canonical = string.Join('\n', "batch-settings-v1", LanguageCode, VoiceStableId, Engine, FailurePolicy.ToString());
        if (SkipUrls || UrlTransformVersion is not null)
            canonical += "\nurl-narration-v1\n" + SkipUrls + "\n" + UrlTransformVersion;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
