using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Offline immutable proposal. Creation confers no submission authority or voice compatibility.</summary>
public sealed class GoogleLongAudioRequest
{
    internal const int MaximumInputBytes = 1_000_000;
    // Default JSON escaping expands one input UTF-8 byte to at most six ASCII bytes.
    // The pinned identifier/output fields add less than 4096 bytes; this is not an API sentence limit.
    internal const int MaximumWireBytes = MaximumInputBytes * 6 + 4096;
    private readonly byte[] _payload;

    private GoogleLongAudioRequest(Guid attemptId, string projectNumber, string location, string credentialReferenceId,
        string outputGcsUri, byte[] payload, int inputUtf8Bytes, int conservativeCharacterUnits, string languageCode, string voiceName, string audioEncoding)
    {
        AttemptId = attemptId; ProjectNumber = projectNumber; Location = location;
        CredentialReferenceId = credentialReferenceId; OutputGcsUri = outputGcsUri; _payload = payload;
        InputUtf8Bytes = inputUtf8Bytes; ConservativeCharacterUnits = conservativeCharacterUnits;
        LanguageCode = languageCode; VoiceName = voiceName; AudioEncoding = audioEncoding;
        PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        BindingSha256 = GoogleLongAudioValidation.BindingDigest(attemptId, projectNumber, location,
            credentialReferenceId, outputGcsUri, PayloadSha256);
    }

    public Guid AttemptId { get; }
    public string ProjectNumber { get; }
    public string Location { get; }
    public string CredentialReferenceId { get; }
    public string OutputGcsUri { get; }
    public string PayloadSha256 { get; }
    public string BindingSha256 { get; }
    public int PayloadBytes => _payload.Length;
    public int InputUtf8Bytes { get; }
    /// <summary>UTF-16 units are a conservative upper bound on Unicode scalar character count, not a provider sentence limit.</summary>
    public int ConservativeCharacterUnits { get; }
    public string LanguageCode { get; }
    public string VoiceName { get; }
    public string AudioEncoding { get; }
    public byte[] CopyPayload() => (byte[])_payload.Clone();
    public override string ToString() => "Long Audio proposal (private request fields omitted).";

    public static GoogleLongAudioRequest Create(Guid attemptId, string projectNumber, string location,
        string credentialReferenceId, string outputGcsUri, string exactText, string languageCode, string voiceName,
        string audioEncoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactText);
        if (new UTF8Encoding(false, true).GetByteCount(exactText) > MaximumInputBytes)
            throw new ArgumentException("Long Audio proposal exceeds its local input bound.", nameof(exactText));
        GoogleLongAudioValidation.RequireScope(attemptId, projectNumber, location, credentialReferenceId, outputGcsUri);
        GoogleLongAudioValidation.RequireIdentifier(languageCode, 64);
        GoogleLongAudioValidation.RequireIdentifier(voiceName, 128);
        if (audioEncoding is not ("MP3" or "LINEAR16")) throw new ArgumentException("Unsupported local audio format.", nameof(audioEncoding));
        string extension = audioEncoding is "MP3" ? ".mp3" : ".wav";
        if (!outputGcsUri.EndsWith(extension, StringComparison.Ordinal))
            throw new ArgumentException("Output object extension must match the exact audio format.", nameof(outputGcsUri));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = new { text = exactText },
            voice = new { languageCode, name = voiceName },
            audioConfig = new { audioEncoding },
            outputGcsUri,
        });
        if (payload.Length > MaximumWireBytes) throw new ArgumentException("Compiled Long Audio proposal exceeds its derived serialization extent.", nameof(exactText));
        return new(attemptId, projectNumber, location, credentialReferenceId, outputGcsUri, payload,
            new UTF8Encoding(false, true).GetByteCount(exactText), exactText.Length, languageCode, voiceName, audioEncoding);
    }
}
