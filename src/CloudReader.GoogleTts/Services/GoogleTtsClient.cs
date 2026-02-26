using CloudReader.Core.Models;
using CloudReader.GoogleTts.Models;
using Google.Api.Gax;
using Google.Cloud.TextToSpeech.V1;
using Google.Cloud.TextToSpeech.V1Beta1;
using Polly;

namespace CloudReader.GoogleTts.Services;

public sealed class GoogleTtsClient
{
    private readonly TextToSpeechClient _v1;
    private readonly TextToSpeechLongAudioSynthesizeClient _v1Beta1;
    private readonly ResiliencePipeline _retry;

    public GoogleTtsClient(TextToSpeechClient v1, TextToSpeechLongAudioSynthesizeClient v1Beta1)
    {
        _v1 = v1;
        _v1Beta1 = v1Beta1;
        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 5,
                Delay = TimeSpan.FromMilliseconds(250),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Grpc.Core.RpcException>(ex =>
                    ex.StatusCode is Grpc.Core.StatusCode.ResourceExhausted or Grpc.Core.StatusCode.Unavailable)
            })
            .Build();
    }

    public async Task<IReadOnlyList<VoiceCatalogItem>> ListVoicesAsync(CancellationToken ct)
    {
        var response = await _v1.ListVoicesAsync(new ListVoicesRequest(), cancellationToken: ct);
        return response.Voices.Select(v => new VoiceCatalogItem(
            v.Name,
            v.LanguageCodes,
            v.SsmlGender.ToString(),
            v.NaturalSampleRateHertz,
            InferTier(v.Name))).ToList();
    }

    public async Task<byte[]> SynthesizeAsync(TtsRequest request, CancellationToken ct)
    {
        return await _retry.ExecuteAsync(async _ =>
        {
            var response = await _v1.SynthesizeSpeechAsync(new SynthesizeSpeechRequest
            {
                Input = request.IsSsml ? new SynthesisInput { Ssml = request.Input } : new SynthesisInput { Text = request.Input },
                Voice = new VoiceSelectionParams { Name = request.VoiceName, LanguageCode = request.LanguageCode },
                AudioConfig = new AudioConfig
                {
                    AudioEncoding = Enum.Parse<AudioEncoding>(request.AudioEncoding, true),
                    SpeakingRate = request.SpeakingRate,
                    Pitch = request.Pitch,
                    VolumeGainDb = request.VolumeGainDb,
                    SampleRateHertz = request.SampleRateHertz ?? 0
                }
            }, cancellationToken: ct);
            return response.AudioContent.ToByteArray();
        }, ct);
    }

    public async Task<string> StartLongAudioAsync(string projectId, string location, string input, string outputGcsUri, string voiceName, string languageCode, CancellationToken ct)
    {
        var parent = $"projects/{projectId}/locations/{location}";
        var operation = await _v1Beta1.SynthesizeLongAudioAsync(new SynthesizeLongAudioRequest
        {
            Parent = parent,
            Input = new Google.Cloud.TextToSpeech.V1Beta1.SynthesisInput { Text = input },
            AudioConfig = new Google.Cloud.TextToSpeech.V1Beta1.AudioConfig { AudioEncoding = Google.Cloud.TextToSpeech.V1Beta1.AudioEncoding.Mp3 },
            Voice = new Google.Cloud.TextToSpeech.V1Beta1.VoiceSelectionParams { Name = voiceName, LanguageCode = languageCode },
            OutputGcsUri = outputGcsUri
        }, CallSettings.FromCancellationToken(ct));

        return operation.Name;
    }

    public async Task<bool> PollLongAudioCompleteAsync(string operationName, CancellationToken ct)
    {
        var operation = await _v1Beta1.PollOnceSynthesizeLongAudioAsync(operationName, CallSettings.FromCancellationToken(ct));
        return operation.IsCompleted;
    }

    private static string InferTier(string voiceName)
    {
        if (voiceName.Contains("Chirp3-HD", StringComparison.OrdinalIgnoreCase)) return "Chirp3-HD";
        if (voiceName.Contains("Neural2", StringComparison.OrdinalIgnoreCase)) return "Neural2";
        if (voiceName.Contains("Studio", StringComparison.OrdinalIgnoreCase)) return "Studio";
        if (voiceName.Contains("Wavenet", StringComparison.OrdinalIgnoreCase)) return "WaveNet";
        if (voiceName.Contains("Standard", StringComparison.OrdinalIgnoreCase)) return "Standard";
        return "Unknown";
    }
}
