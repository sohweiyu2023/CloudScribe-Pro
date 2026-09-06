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

    internal CurrentIntent PublishCoherentCapture(
        GoogleGenerationProductionRequestIntent intent,
        GoogleGenerationProductionAuthorizationSnapshotStateOwner authorizationOwner,
        GoogleGenerationProductionAuthorizationSnapshotStateOwner.AuthorizationSnapshot authorizationSnapshot)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(authorizationOwner);
        ArgumentNullException.ThrowIfNull(authorizationSnapshot);
        intent.Validate();
        authorizationSnapshot.Validate();
        ValidateCaptureBinding(intent, authorizationSnapshot);

        lock (_gate)
        {
            long nextVersion = checked(_version + 1);

            // Publish authorization while the intent gate is held. AssembleCurrentAsync cannot
            // claim either the prior or new intent between these two publications, so the new
            // intent becomes observable only after its matching authorization snapshot exists.
            authorizationOwner.Publish(authorizationSnapshot);
            _current = intent;
            _version = nextVersion;
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

    private static void ValidateCaptureBinding(
        GoogleGenerationProductionRequestIntent intent,
        GoogleGenerationProductionAuthorizationSnapshotStateOwner.AuthorizationSnapshot authorizationSnapshot)
    {
        if (!string.Equals(intent.AccountId, authorizationSnapshot.AccountId, StringComparison.Ordinal)
            || !string.Equals(intent.ProjectId, authorizationSnapshot.ProjectId, StringComparison.Ordinal)
            || !string.Equals(intent.ModelId, authorizationSnapshot.ModelId, StringComparison.Ordinal)
            || !string.Equals(intent.IdempotencyKey, authorizationSnapshot.IdempotencyKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Google generation production capture authorization does not match the request intent identity.");
        }

        if (authorizationSnapshot.CapturedAtUtc < intent.CapturedAtUtc)
        {
            throw new InvalidOperationException(
                "Google generation production capture authorization predates the request intent.");
        }
    }

    public sealed record CurrentIntent(
        long Version,
        GoogleGenerationProductionRequestIntent Intent);
}
