using System.Security.Cryptography;
using System.Text;
using CloudScribe.Domain.Generation;

namespace CloudScribe.App.Composition;

public sealed record GoogleVoiceSampleSpec(VoiceLabCatalogSelection Voice, string LanguageCode, string Text)
{
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        "cloudscribe-verified-mp3-sample-v1", Voice.ProviderStableId, Voice.AccountStableId,
        Voice.ProjectStableId, Voice.VoiceStableId, Voice.VoiceFingerprint, LanguageCode,
        "MP3|speakingRate=1|pitch=0|volumeGainDb=0", Text)))).ToLowerInvariant();

    public void Validate()
    {
        Voice.Validate();
        if (string.IsNullOrWhiteSpace(Text) || Text.Length > 500 || Text.Contains('\0'))
            throw new InvalidOperationException("Enter 1 to 500 sample characters; document text is never substituted.");
        if (Voice.LanguageCodes is null || !Voice.LanguageCodes.Contains(LanguageCode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The sample language must be explicitly returned for this voice by the provider.");
        if (!GoogleVoiceComparisonInformation.SupportsSampleFamily(Voice.VoiceStableId))
            throw new InvalidOperationException("This voice family is reference-only in the short MP3 sample flow.");
    }
}
