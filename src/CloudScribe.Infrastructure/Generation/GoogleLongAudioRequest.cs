using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Offline immutable proposal. Creation confers no submission authority or voice compatibility.</summary>
public sealed class GoogleLongAudioRequest
{
    private readonly byte[] _payload;

    private GoogleLongAudioRequest(Guid attemptId, string projectNumber, string location, string credentialReferenceId,
        string outputGcsUri, byte[] payload)
    {
        AttemptId = attemptId; ProjectNumber = projectNumber; Location = location;
        CredentialReferenceId = credentialReferenceId; OutputGcsUri = outputGcsUri; _payload = payload;
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
    public byte[] CopyPayload() => (byte[])_payload.Clone();
    public override string ToString() => "Long Audio proposal (private request fields omitted).";

    public static GoogleLongAudioRequest Create(Guid attemptId, string projectNumber, string location,
        string credentialReferenceId, string outputGcsUri, string exactText, string languageCode, string voiceName,
        string audioEncoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactText);
        if (exactText.Length > 1_000_000 || new UTF8Encoding(false, true).GetByteCount(exactText) > 1_000_000)
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
        if (payload.Length > 1_000_000) throw new ArgumentException("Compiled Long Audio proposal exceeds its local byte bound.", nameof(exactText));
        return new(attemptId, projectNumber, location, credentialReferenceId, outputGcsUri, payload);
    }
}
