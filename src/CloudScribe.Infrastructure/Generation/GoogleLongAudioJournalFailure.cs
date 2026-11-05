namespace CloudScribe.Infrastructure.Generation;

/// <summary>Safe observed-response context on an unchanged local persistence exception; never paid authority.</summary>
public sealed record GoogleLongAudioJournalFailure(int ObservedHttpStatus, GoogleLongAudioObservation Observation)
{
    private static readonly object MetadataKey = new();
    public override string ToString() => "Long Audio journal failure metadata (private context omitted).";

    internal static void Attach(Exception error, int status, GoogleLongAudioObservation observation)
    {
        try { error.Data[MetadataKey] = new GoogleLongAudioJournalFailure(status, observation); }
        catch (Exception metadataError) when (NonFatal(metadataError)) { }
    }

    public static GoogleLongAudioJournalFailure? Inspect(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        try { return error.Data[MetadataKey] as GoogleLongAudioJournalFailure; }
        catch (Exception metadataError) when (NonFatal(metadataError)) { return null; }
    }

    private static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}
