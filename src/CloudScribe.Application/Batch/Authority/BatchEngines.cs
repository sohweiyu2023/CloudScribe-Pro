namespace CloudScribe.Application.Batch.Authority;

/// <summary>Narration engines. A batch's setting is a preference; each file's actual engine is fixed when it is approved.</summary>
public static class BatchEngines
{
    /// <summary>Ordinary complete-article engine (sentence-sized requests joined into one MP3).</summary>
    public const string CompleteArticle = "complete-article";

    /// <summary>Google Long Audio: the whole file as one request, split only after a confirmed applicable failure.</summary>
    public const string LongAudio = "long-audio";

    /// <summary>Long Audio for every file it supports; the ordinary engine for the rest, shown per file before approval.</summary>
    public const string LongAudioFirst = "long-audio-first";

    public static bool IsKnown(string? engine) => IsFileEngine(engine) || string.Equals(engine, LongAudioFirst, StringComparison.Ordinal);

    public static bool IsFileEngine(string? engine) =>
        string.Equals(engine, CompleteArticle, StringComparison.Ordinal) || string.Equals(engine, LongAudio, StringComparison.Ordinal);

    /// <summary>The file engines a batch preference may use, in order of preference.</summary>
    public static IReadOnlyList<string> Candidates(string preference) => preference switch
    {
        LongAudioFirst => [LongAudio, CompleteArticle],
        LongAudio => [LongAudio],
        _ => [CompleteArticle],
    };

    public static string Describe(string? engine) => engine switch
    {
        LongAudio => "Long Audio",
        CompleteArticle => "ordinary engine",
        LongAudioFirst => "Long Audio first",
        _ => "engine not chosen yet",
    };
}
