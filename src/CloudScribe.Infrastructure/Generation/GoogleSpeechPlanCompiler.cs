using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleSpeechCompilationOptions(
    string LanguageCode,
    string VoiceName,
    string AudioEncoding,
    int MaximumPayloadBytes)
{
    public GoogleSpeechCompilationOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LanguageCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(AudioEncoding);
        if (MaximumPayloadBytes is < 256 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes));
        }
        return this;
    }
}

public sealed record GoogleSpeechCompilation(
    ReadOnlyMemory<byte> Payload,
    IReadOnlyList<SpeechDegradation> Degradations,
    string PayloadSha256);

public static class GoogleSpeechPlanCompiler
{
    public static GoogleSpeechCompilation Compile(SpeechPlan plan, GoogleSpeechCompilationOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var text = new StringBuilder();
        var degradations = new List<SpeechDegradation>();
        var rate = 1m;
        var pitch = 0m;
        var volume = 0m;

        for (var i = 0; i < plan.Nodes.Count; i++)
        {
            switch (plan.Nodes[i])
            {
                case SpeechText node:
                    text.Append(node.Text);
                    break;
                case SpeechPronunciation node:
                    text.Append(node.Text);
                    degradations.Add(new SpeechDegradation(i, SpeechDegradationKind.Approximated,
                        "Provider request compiler preserves pronunciation text but cannot guarantee the requested phoneme alphabet in the plain-text request shape.", null));
                    break;
                case SpeechPause node when node.Duration > TimeSpan.Zero:
                    text.Append(' ');
                    degradations.Add(new SpeechDegradation(i, SpeechDegradationKind.Approximated,
                        "Pause is approximated as whitespace in the deterministic plain-text Google request compiler.", null));
                    break;
                case SpeechProsody node:
                    rate = node.Rate;
                    pitch = node.PitchSemitones;
                    volume = node.VolumeDb;
                    break;
                case SpeechVoice:
                    degradations.Add(new SpeechDegradation(i, SpeechDegradationKind.Omitted,
                        "Per-node voice changes require a multi-request split and are not silently applied by this compiler.", null));
                    break;
                case SpeechEmphasis:
                case SpeechMark:
                case SpeechTimestampRequest:
                case SpeechChapter:
                case SpeechSpeakerChange:
                    degradations.Add(new SpeechDegradation(i, SpeechDegradationKind.Omitted,
                        "Canonical feature is not represented by the deterministic plain-text Google request shape.", null));
                    break;
            }
        }

        if (text.Length == 0)
        {
            throw new InvalidOperationException("Google compilation produced no speakable text.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = new { text = text.ToString() },
            voice = new { languageCode = options.LanguageCode, name = options.VoiceName },
            audioConfig = new
            {
                audioEncoding = options.AudioEncoding,
                speakingRate = rate,
                pitch,
                volumeGainDb = volume,
            },
        });

        if (payload.Length > options.MaximumPayloadBytes)
        {
            throw new InvalidOperationException($"Compiled Google request is {payload.Length} bytes, exceeding the admitted post-compile limit of {options.MaximumPayloadBytes} bytes.");
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        return new GoogleSpeechCompilation(payload, degradations.ToArray(), hash);
    }
}
