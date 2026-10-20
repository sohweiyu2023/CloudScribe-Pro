using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Plans local text parts against the existing compiler, without provider access.</summary>
public static class LocalNarrationChunker
{
    public static IReadOnlyList<string> Split(string text, string language, string voice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        List<string> result = [];
        int offset = 0;
        while (offset < text.Length)
        {
            if (result.Count >= 100) throw new InvalidOperationException("Use a shorter source section; at most 100 local parts can be created at once.");
            int length = FindLength(text, offset, language, voice);
            if (length == 0) throw new InvalidOperationException("This text or voice cannot be compiled into a short local narration part.");
            if (offset + length < text.Length)
            {
                int boundary = text.LastIndexOfAny(['\n', ' ', '\t'], offset + length - 1, length);
                if (boundary >= offset + length / 2) length = boundary - offset + 1;
            }
            result.Add(text.Substring(offset, length)); offset += length;
        }
        return result;
    }

    private static int FindLength(string text, int offset, string language, string voice)
    {
        int low = 1; int high = Math.Min(4_000, text.Length - offset); int best = 0;
        while (low <= high)
        {
            int length = low + (high - low) / 2;
            int safeLength = length;
            if (offset + safeLength < text.Length && char.IsHighSurrogate(text[offset + safeLength - 1])) safeLength--;
            if (safeLength == 0) { low = length + 1; continue; }
            string candidate = text.Substring(offset, safeLength);
            if (string.IsNullOrWhiteSpace(candidate)) { low = length + 1; continue; }
            try
            {
                var plan = new SpeechPlan(language, [new SpeechText(candidate)], "local-parts-preview");
                _ = GoogleSpeechPlanCompiler.Compile(plan, new GoogleSpeechCompilationOptions(language, voice, "MP3", 5_000));
                best = safeLength; low = length + 1;
            }
            catch (InvalidOperationException) { high = length - 1; }
        }
        return best;
    }
}
