namespace CloudScribe.Infrastructure.Generation;

/// <summary>Optional metrics must not replace a provider outcome or obscure its original failure.</summary>
public static class GoogleLongAudioRunMetrics
{
    public static async Task<bool> TryRecordAsync(Func<Task> record, Action<Exception> report)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(report);
        try { await record().ConfigureAwait(false); return true; }
        catch (Exception error) when (NonFatal(error))
        {
            try { report(error); }
            catch (Exception reportingError) when (NonFatal(reportingError)) { /* Caller exposes the false result even if diagnostics are unavailable. */ }
            return false;
        }
    }

    private static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}
