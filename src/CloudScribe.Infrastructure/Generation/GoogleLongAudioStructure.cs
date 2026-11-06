using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Content-free descriptive punctuation statistics over the full allowed plain-text input; not a sentence limit.</summary>
public static class GoogleLongAudioStructure
{
    public static void Log(ILogger logger, GoogleLongAudioRequest request, int splitDepth = 0)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        using var json = JsonDocument.Parse(request.CopyPayload());
        string text = json.RootElement.GetProperty("input").GetProperty("text").GetString()!;
        List<int> lengths = []; int scalars = 0, length = 0, bytes = 0, maximumBytes = 0; bool ended = false;
        foreach (var rune in text.EnumerateRunes())
        {
            bool punctuation = rune.Value is '.' or '?' or '!' or 0x3002 or 0xff01 or 0xff1f or '\n';
            bool tail = punctuation || Rune.IsWhiteSpace(rune) || rune.Value is '"' or '\'' or 0x201d or 0x2019 or 0x300d or 0x300f;
            if (ended && !tail) { lengths.Add(length); maximumBytes = Math.Max(maximumBytes, bytes); length = bytes = 0; ended = false; }
            scalars++; length++; bytes += rune.Utf8SequenceLength; ended |= punctuation;
        }
        if (length > 0) { lengths.Add(length); maximumBytes = Math.Max(maximumBytes, bytes); }
        lengths.Sort();
        string histogram = string.Join(',', new[] { lengths.Count(x => x <= 32), lengths.Count(x => x is > 32 and <= 64),
            lengths.Count(x => x is > 64 and <= 128), lengths.Count(x => x is > 128 and <= 256), lengths.Count(x => x > 256) }
            .Select(x => x.ToString(CultureInfo.InvariantCulture)));
        string correlation = request.AttemptId.ToString("N");
        string? voice = GoogleRequestStructureEvidence.IsPublicVoiceIdentity(request.VoiceName) ? request.VoiceName : null;
        int percentile = lengths[(int)Math.Ceiling(lengths.Count * .95) - 1];
        CloudScribeLog.GoogleInputStructure(logger, correlation, request.PayloadSha256, voice, "Text", request.InputUtf8Bytes,
            scalars, lengths.Count, lengths[^1], maximumBytes, lengths[(lengths.Count - 1) / 2], percentile,
            histogram, false, true, splitDepth);
    }
}
