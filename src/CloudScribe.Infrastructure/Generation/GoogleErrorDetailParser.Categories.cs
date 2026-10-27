using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static partial class GoogleErrorDetailParser
{
    private static GoogleErrorDomainCategory Domain(string? value) => value switch
    {
        null => GoogleErrorDomainCategory.NotProvided,
        "googleapis.com" => GoogleErrorDomainCategory.GoogleApis,
        "texttospeech.googleapis.com" => GoogleErrorDomainCategory.GoogleTextToSpeech,
        _ => GoogleErrorDomainCategory.Unknown,
    };

    private static GoogleErrorReasonCategory Reason(string? value) => value switch
    {
        null => GoogleErrorReasonCategory.NotProvided,
        "SERVICE_DISABLED" => GoogleErrorReasonCategory.ServiceDisabled,
        "BILLING_DISABLED" => GoogleErrorReasonCategory.BillingDisabled,
        "API_KEY_INVALID" => GoogleErrorReasonCategory.ApiKeyInvalid,
        "API_KEY_SERVICE_BLOCKED" => GoogleErrorReasonCategory.ApiKeyServiceBlocked,
        "RATE_LIMIT_EXCEEDED" => GoogleErrorReasonCategory.RateLimitExceeded,
        "RESOURCE_QUOTA_EXCEEDED" => GoogleErrorReasonCategory.ResourceQuotaExceeded,
        "CONSUMER_INVALID" => GoogleErrorReasonCategory.ConsumerInvalid,
        "ACCESS_TOKEN_EXPIRED" => GoogleErrorReasonCategory.AccessTokenExpired,
        "ACCESS_TOKEN_SCOPE_INSUFFICIENT" => GoogleErrorReasonCategory.AccessTokenScopeInsufficient,
        _ => GoogleErrorReasonCategory.Unknown,
    };

    private static GoogleErrorFieldCategory Fields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return GoogleErrorFieldCategory.NotProvided;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 16) return GoogleErrorFieldCategory.Unknown;
        var fields = element.EnumerateArray().Select(x => Field(StringValue(Single(x, "field")))).Distinct().ToArray();
        if (fields.Length == 0) return GoogleErrorFieldCategory.NotProvided;
        if (fields.Contains(GoogleErrorFieldCategory.Unknown)) return GoogleErrorFieldCategory.Unknown;
        return fields.Length == 1 ? fields[0] : GoogleErrorFieldCategory.Multiple;
    }

    private static GoogleErrorFieldCategory Field(string? value) => value switch
    {
        "input.text" => GoogleErrorFieldCategory.InputText,
        "input.ssml" => GoogleErrorFieldCategory.InputSsml,
        "voice.name" => GoogleErrorFieldCategory.VoiceName,
        "voice.language_code" or "voice.languageCode" => GoogleErrorFieldCategory.VoiceLanguageCode,
        "audio_config.audio_encoding" or "audioConfig.audioEncoding" => GoogleErrorFieldCategory.AudioEncoding,
        "audio_config.speaking_rate" or "audioConfig.speakingRate" => GoogleErrorFieldCategory.SpeakingRate,
        "audio_config.pitch" or "audioConfig.pitch" => GoogleErrorFieldCategory.Pitch,
        "audio_config.volume_gain_db" or "audioConfig.volumeGainDb" => GoogleErrorFieldCategory.VolumeGain,
        _ => GoogleErrorFieldCategory.Unknown,
    };
}
