using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CloudScribe.Infrastructure.Generation;

public static partial class GoogleSpeechPlanCompiler
{
    /// <summary>
    /// Pure proposal for documented synchronous Chirp HD sentence markers.
    /// Different bytes require their own independently admitted request identity;
    /// this does not reuse a plain-text authorization or dispatch anything.
    /// </summary>
    public static GoogleSpeechCompilation CompileSentenceMarkedDiagnostic(string exactText,
        GoogleSpeechCompilationOptions options, IReadOnlyList<int> sentenceEnds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactText);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sentenceEnds);
        options.Validate();
        if (exactText.Length > 2_000)
            throw new InvalidOperationException("Sentence diagnostic text exceeds its bounded review size.");
        if (!string.Equals(options.LanguageCode, "cmn-CN", StringComparison.Ordinal)
            || !string.Equals(options.VoiceName, "cmn-CN-Chirp3-HD-Alnilam", StringComparison.Ordinal)
            || !string.Equals(options.AudioEncoding, "MP3", StringComparison.Ordinal) || options.MaximumPayloadBytes > 5_000)
            throw new InvalidOperationException("Sentence diagnostic requires the exact reviewed Mandarin Alnilam MP3 format.");
        var runes = exactText.EnumerateRunes().ToArray();
        if (runes.Length > 1_000 || sentenceEnds.Count is < 1 or > 32 || sentenceEnds[^1] != runes.Length)
            throw new InvalidOperationException("Sentence markers must cover the complete bounded original text.");
        var graphemeStarts = StringInfo.ParseCombiningCharacters(exactText).ToHashSet();
        int[] utf16Ends = new int[runes.Length + 1];
        for (int i = 0; i < runes.Length; i++) utf16Ends[i + 1] = utf16Ends[i] + runes[i].Utf16SequenceLength;
        var xml = new StringBuilder();
        using (var writer = XmlWriter.Create(xml, new XmlWriterSettings
        { OmitXmlDeclaration = true, CheckCharacters = true, NewLineHandling = NewLineHandling.Entitize }))
        {
            writer.WriteStartElement("speak");
            int start = 0;
            foreach (int end in sentenceEnds)
            {
                if (end <= start || end > runes.Length
                    || end < runes.Length && (!graphemeStarts.Contains(utf16Ends[end]) || !ExistingEnd(runes, end)))
                    throw new InvalidOperationException("Only explicitly reviewed existing sentence or newline boundaries are permitted.");
                string slice = string.Concat(runes[start..end].Select(r => r.ToString()));
                if (string.IsNullOrWhiteSpace(slice)) writer.WriteString(slice);
                else writer.WriteElementString("s", slice);
                start = end;
            }
            writer.WriteEndElement();
        }
        string ssml = xml.ToString();
        using (var reader = XmlReader.Create(new StringReader(ssml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_384 }))
        {
            var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            if (root.Name != "speak" || root.HasAttributes || root.Elements().Any(e => e.Name != "s" || e.HasAttributes || e.HasElements)
                || !string.Equals(root.Value, exactText, StringComparison.Ordinal))
                throw new InvalidOperationException("Sentence markup must preserve every original character without additional directives.");
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = new { ssml },
            voice = new { languageCode = options.LanguageCode, name = options.VoiceName },
            audioConfig = new { audioEncoding = options.AudioEncoding }
        });
        if (payload.Length > options.MaximumPayloadBytes)
            throw new InvalidOperationException("Sentence-marked request exceeds the exact compiled byte bound.");
        return new(payload, [], Convert.ToHexStringLower(SHA256.HashData(payload)));
    }

    private static bool ExistingEnd(Rune[] runes, int end)
    {
        int i = end - 1;
        if (runes[i].Value is '\r' or '\n') return true;
        while (i >= 0 && (Rune.IsWhiteSpace(runes[i]) || Closing(runes[i]))) i--;
        return i >= 0 && runes[i].Value is '.' or '!' or '?' or '\u3002' or '\uff01' or '\uff1f';
    }

    /// <summary>Deterministic markers at existing sentence punctuation/newlines; never changes literal source.</summary>
    public static GoogleSentenceMarkers ExistingSentenceMarkers(string exactText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactText);
        if (exactText.Length > 2_000) throw new InvalidOperationException("Sentence text exceeds its bounded size.");
        var runes = exactText.EnumerateRunes().ToArray();
        if (runes.Length > 1_000) throw new InvalidOperationException("Sentence text exceeds its scalar bound.");
        List<int> ends = []; int start = 0, i = 0;
        while (i < runes.Length)
        {
            if (!Strong(runes[i])) { i++; continue; }
            int stop = i + 1;
            while (stop < runes.Length && (Strong(runes[stop]) || Closing(runes[stop]) || Rune.IsWhiteSpace(runes[stop]))) stop++;
            if (runes[start..stop].Any(Rune.IsLetterOrDigit)) { ends.Add(stop); start = stop; }
            i = stop;
        }
        if (ends.Count == 0 || ends[^1] != runes.Length) ends.Add(runes.Length);
        if (ends.Count > 32) throw new InvalidOperationException("Existing sentence markers exceed their bounded count.");
        return new(1, ends.AsReadOnly());
    }

    private static bool Strong(Rune r) => r.Value is '!' or '?' or '\r' or '\n' or 0x3002 or 0xff01 or 0xff1f;
    private static bool Closing(Rune r) => r.Value is '"' or '\'' or 0x201d or 0x2019 or ')' or 0x300d or 0x300f or 0xff09 or 0x300b or 0x3011;

    /// <summary>Strict inert projection for conservative billing; callers must also verify exact recompiled wire bytes.</summary>
    public static string ReadSentenceMarkedText(string ssml)
    {
        ArgumentNullException.ThrowIfNull(ssml);
        if (ssml.Length > 16_384) throw new InvalidOperationException("Sentence input exceeds its projection bound.");
        using var reader = XmlReader.Create(new StringReader(ssml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_384 });
        var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        if (root.Name != "speak" || root.HasAttributes || root.Elements().Count() is < 1 or > 32
            || root.Elements().Any(e => e.Name != "s" || e.HasAttributes || e.Nodes().Any(n => n is not XText))
            || root.Nodes().Any(n => n is not XElement && (n is not XText text || !string.IsNullOrWhiteSpace(text.Value))))
            throw new InvalidOperationException("Only literal sentence text and whitespace are permitted in this reviewed format.");
        return root.Value;
    }
}
