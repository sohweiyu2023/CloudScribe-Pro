namespace CloudScribe.Application.Logging;

/// <summary>Content-free source validation and durable import outcomes, never provider outcomes.</summary>
public enum BatchSourceOutcome
{
    DraftSourceReady, DraftSourceRefreshed, SourceUnavailable, SourceChanged, ImportInterrupted, Imported, ImportRefused,
    NarrationPrepared, NarrationReused, NarrationUnchanged, NarrationEmpty, NarrationInvalidated,
}
