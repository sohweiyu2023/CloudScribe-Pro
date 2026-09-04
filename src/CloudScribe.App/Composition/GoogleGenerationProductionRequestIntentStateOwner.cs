namespace CloudScribe.App.Composition;

/// <summary>
/// Atomically owns the current user/request-originated Stage6 intent. Authorization evidence is
/// deliberately excluded so downstream production composition cannot inherit caller assertions.
/// </summary>
public sealed class GoogleGenerationProductionRequestIntentStateOwner
{
    private readonly System.Threading.Lock _gate = new();
    private GoogleGenerationProductionRequestIntent? _current;
    private long _version;

    public CurrentIntent Publish(GoogleGenerationProductionRequestIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent.Validate();

        lock (_gate)
        {
            _current = intent;
            _version = checked(_version + 1);
            return new CurrentIntent(_version, intent);
        }
    }

    public CurrentIntent ClaimCurrent()
    {
        lock (_gate)
        {
            GoogleGenerationProductionRequestIntent intent = _current
                ?? throw new InvalidOperationException(
                    "No coherent current Google generation request intent is available.");
            CurrentIntent claimed = new(_version, intent);
            _current = null;
            return claimed;
        }
    }

    public void RestoreIfUnchanged(CurrentIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        lock (_gate)
        {
            if (_current is null && _version == intent.Version)
            {
                _current = intent.Intent;
            }
        }
    }

    public sealed record CurrentIntent(
        long Version,
        GoogleGenerationProductionRequestIntent Intent);
}
