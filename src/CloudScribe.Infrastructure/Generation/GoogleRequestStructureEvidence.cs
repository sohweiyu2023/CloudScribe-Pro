using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Content-free structural observations. Boundaries are descriptive, never a provider acceptance threshold.</summary>
public sealed partial record GoogleRequestStructureEvidence(string InputMode, string? VoiceName, int InputUtf8Bytes,
    int SourceScalars, int Sentences, int MaximumSentenceScalars, int MaximumSentenceUtf8Bytes,
    int MedianSentenceScalars, int P95SentenceScalars, int UpTo32, int From33To64, int From65To128,
    int From129To256, int Over256, bool ExplicitSentenceMarkers, bool Complete)
{
    public static GoogleRequestStructureEvidence Measure(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > 65_536) return Unavailable;
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            string? voice = root.GetProperty("voice").GetProperty("name").GetString();
            if (!IsPublicVoiceIdentity(voice)) voice = null;
            var input = root.GetProperty("input");
            bool ssml = input.TryGetProperty("ssml", out var field);
            string mode = ssml ? "Ssml" : "Text";
            if (!ssml) field = input.GetProperty("text");
            string value = field.GetString() ?? throw new InvalidDataException("Input unavailable.");
            if (value.Length > 16_384) return Unavailable;
            string source = value; string[] sentences; bool explicitMarkers = false;
            if (ssml)
            {
                using var reader = XmlReader.Create(new StringReader(value), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_384 });
                var xml = XElement.Load(reader, LoadOptions.PreserveWhitespace);
                source = xml.Value;
                sentences = xml.Descendants("s").Select(x => x.Value).Take(1001).ToArray();
                explicitMarkers = sentences.Length is > 0 and <= 1000
                    && string.Equals(string.Concat(sentences), source, StringComparison.Ordinal);
                if (!explicitMarkers) sentences = ExistingUnits(source);
            }
            else sentences = ExistingUnits(source);
            var lengths = sentences.Select(x => x.EnumerateRunes().Count()).Order().ToArray();
            if (lengths.Length is < 1 or > 1000) return Unavailable;
            return new(mode, voice, Encoding.UTF8.GetByteCount(value), source.EnumerateRunes().Count(), lengths.Length,
                lengths[^1], sentences.Max(Encoding.UTF8.GetByteCount), lengths[(lengths.Length - 1) / 2],
                lengths[(int)Math.Ceiling(lengths.Length * .95) - 1], lengths.Count(x => x <= 32),
                lengths.Count(x => x is > 32 and <= 64), lengths.Count(x => x is > 64 and <= 128),
                lengths.Count(x => x is > 128 and <= 256), lengths.Count(x => x > 256), explicitMarkers, voice is not null);
        }
        catch (Exception error) when (error is JsonException or XmlException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return Unavailable; }
    }

    private static string[] ExistingUnits(string source)
    {
        var markers = GoogleSpeechPlanCompiler.ExistingSentenceMarkers(source);
        var runes = source.EnumerateRunes().ToArray(); List<string> result = []; int start = 0;
        foreach (int end in markers.EndScalars)
        { result.Add(string.Concat(runes[start..end].Select(x => x.ToString()))); start = end; }
        return result.ToArray();
    }

    private static GoogleRequestStructureEvidence Unavailable => new("Unavailable", null, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, false, false);
    internal static bool IsPublicVoiceIdentity(string? value) => value is "cmn-CN-Chirp3-HD-Alnilam" or "en-US-Standard-C";
}
