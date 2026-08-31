using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static class GoogleSpeechPlanCompiler
{
    public static GoogleSpeechCompilation Compile(SpeechPlan plan, GoogleSpeechCompilationOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var text = new StringBuilder();
        var degradations = new List<SpeechDegradation>();
        var prosody = new GoogleProsodyState(1m, 0m, 0m);

        for (var i = 0; i < plan.Nodes.Count; i++)
        {
            ProcessNode(plan.Nodes[i], i, text, degradations, ref prosody);
        }

        if (text.Length == 0)
        {
            throw new InvalidOperationException("Google compilation produced no speakable text.");
        }

        var payload = BuildPayload(text, options, prosody);
        if (payload.Length > options.MaximumPayloadBytes)
        {
            throw new InvalidOperationException($"Compiled Google request is {payload.Length} bytes, exceeding the admitted post-compile limit of {options.MaximumPayloadBytes} bytes.");
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        return new GoogleSpeechCompilation(payload, degradations.ToArray(), hash);
    }

    private static void ProcessNode(
        SpeechPlanNode node,
        int index,
        StringBuilder text,
        List<SpeechDegradation> degradations,
        ref GoogleProsodyState prosody)
    {
        switch (node)
        {
            case SpeechText speechText:
                text.Append(speechText.Text);
                break;
            case SpeechPronunciation pronunciation:
                text.Append(pronunciation.Text);
                degradations.Add(new SpeechDegradation(index, SpeechDegradationKind.Approximated,
                    "Provider request compiler preserves pronunciation text but cannot guarantee the requested phoneme alphabet in the plain-text request shape.", null));
                break;
            case SpeechPause pause when pause.Duration > TimeSpan.Zero:
                text.Append(' ');
                degradations.Add(new SpeechDegradation(index, SpeechDegradationKind.Approximated,
                    "Pause is approximated as whitespace in the deterministic plain-text Google request compiler.", null));
                break;
            case SpeechProsody speechProsody:
                prosody = new GoogleProsodyState(speechProsody.Rate, speechProsody.PitchSemitones, speechProsody.VolumeDb);
                break;
            case SpeechVoice:
                degradations.Add(new SpeechDegradation(index, SpeechDegradationKind.Omitted,
                    "Per-node voice changes require a multi-request split and are not silently applied by this compiler.", null));
                break;
            case SpeechEmphasis:
            case SpeechMark:
            case SpeechTimestampRequest:
            case SpeechChapter:
            case SpeechSpeakerChange:
                degradations.Add(new SpeechDegradation(index, SpeechDegradationKind.Omitted,
                    "Canonical feature is not represented by the deterministic plain-text Google request shape.", null));
                break;
        }
    }

    private static byte[] BuildPayload(StringBuilder text, GoogleSpeechCompilationOptions options, GoogleProsodyState prosody) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = new { text = text.ToString() },
            voice = new { languageCode = options.LanguageCode, name = options.VoiceName },
            audioConfig = new
            {
                audioEncoding = options.AudioEncoding,
                speakingRate = prosody.Rate,
                pitch = prosody.Pitch,
                volumeGainDb = prosody.Volume,
            },
        });

    private readonly record struct GoogleProsodyState(decimal Rate, decimal Pitch, decimal Volume);
}
