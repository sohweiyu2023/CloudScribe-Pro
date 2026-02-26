using CloudReader.Core.Models;
using CloudReader.GoogleTts.Models;

namespace CloudReader.GoogleTts.Services;

public interface IGoogleTtsClient
{
    Task<IReadOnlyList<VoiceCatalogItem>> ListVoicesAsync(CancellationToken ct);
    Task<byte[]> SynthesizeAsync(TtsRequest request, CancellationToken ct);
    Task<string> StartLongAudioAsync(string projectId, string location, string input, string outputGcsUri, string voiceName, string languageCode, CancellationToken ct);
    Task<bool> PollLongAudioCompleteAsync(string operationName, CancellationToken ct);
}
