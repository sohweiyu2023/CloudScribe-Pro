using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// A small MP3 sampling surface over the existing Stage6 pipeline. Cache entries are session-only
/// references to accepted output, never arbitrary media or Stage7's deliberately unverified cache.
/// Replay/export revalidate accepted bytes and never invoke preparation, approval or synthesis.
/// </summary>
public sealed class GoogleVoiceSampleSession(
    Func<GoogleVoiceSampleSpec, Guid, CancellationToken, Task<ShellViewModel.GoogleGenerationSpendReview>> prepare,
    Func<long, bool, CancellationToken, Task> approve,
    Func<CancellationToken, Task<string>> generate,
    Func<string, CancellationToken, Task<string>> verify,
    Func<string, CancellationToken, Task> play,
    Func<string, string, CancellationToken, Task<string>> export,
    Action invalidate)
{
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uncertainAttempts = new(StringComparer.Ordinal);
    private string? _preparedKey;
    private int _inFlight;

    public ShellViewModel.GoogleGenerationSpendReview? Review { get; private set; }
    public bool IsBusy => Volatile.Read(ref _inFlight) != 0;
    public static string CachePolicy => "This-session cache only; accepted MP3 bytes are verified on every replay/export. Restarting clears this cache.";

    public bool HasCached(GoogleVoiceSampleSpec spec) => _cache.ContainsKey(spec.Key);

    public void InvalidateReview()
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current sample operation.");
        ClearReview();
    }

    public async Task<bool> PrepareAsync(GoogleVoiceSampleSpec spec, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            spec.Validate();
            ClearReview();
            if (_cache.TryGetValue(spec.Key, out string? cached))
            {
                await VerifyOrRemoveAsync(spec.Key, cached, cancellationToken).ConfigureAwait(true);
                return true;
            }
            if (_uncertainAttempts.Contains(spec.Key))
                throw new InvalidOperationException("This sample is blocked for this app session after an unresolved attempt. Reconcile the durable queue before restarting and reviewing another request.");
            byte[] identity = Convert.FromHexString(spec.Key);
            Review = await prepare(spec, new Guid(identity.AsSpan(0, 16)), cancellationToken).ConfigureAwait(true);
            Review.Validate();
            if (!string.Equals(Review.VoiceName, spec.Voice.VoiceStableId, StringComparison.Ordinal))
                throw new InvalidOperationException("Prepared voice differs from the selected sample voice.");
            _preparedKey = spec.Key;
            return false;
        }
        catch
        {
            ClearReview();
            throw;
        }
        finally { Exit(); }
    }

    public async Task GenerateAsync(GoogleVoiceSampleSpec spec, bool explicitlyApproved, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            spec.Validate();
            if (!explicitlyApproved || Review is null || !string.Equals(_preparedKey, spec.Key, StringComparison.Ordinal))
                throw new InvalidOperationException("Review and explicitly approve this exact voice, text, language and MP3 settings first.");
            await approve(Review.CurrentEstimateMinorUnits, true, cancellationToken).ConfigureAwait(true);
            // From here a failure may follow dispatch. Never silently retry or substitute old audio.
            _uncertainAttempts.Add(spec.Key);
            string path = await generate(cancellationToken).ConfigureAwait(true);
            await verify(path, cancellationToken).ConfigureAwait(true);
            _cache[spec.Key] = path;
            _uncertainAttempts.Remove(spec.Key);
        }
        finally
        {
            ClearReview();
            Exit();
        }
    }

    public async Task ReplayAsync(GoogleVoiceSampleSpec spec, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            spec.Validate();
            string path = RequireCached(spec);
            await VerifyOrRemoveAsync(spec.Key, path, cancellationToken).ConfigureAwait(true);
            await play(path, cancellationToken).ConfigureAwait(true);
        }
        finally { Exit(); }
    }

    public async Task<string> ExportAsync(GoogleVoiceSampleSpec spec, string destination, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            spec.Validate();
            string path = RequireCached(spec);
            await VerifyOrRemoveAsync(spec.Key, path, cancellationToken).ConfigureAwait(true);
            return await export(path, destination, cancellationToken).ConfigureAwait(true);
        }
        finally { Exit(); }
    }

    private string RequireCached(GoogleVoiceSampleSpec spec) => _cache.TryGetValue(spec.Key, out string? path)
        ? path : throw new InvalidOperationException("No verified matching sample exists in this session.");

    private async Task VerifyOrRemoveAsync(string key, string path, CancellationToken cancellationToken)
    {
        try { await verify(path, cancellationToken).ConfigureAwait(true); }
        catch { _cache.Remove(key); throw; }
    }

    private void ClearReview()
    {
        Review = null;
        _preparedKey = null;
        invalidate();
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            throw new InvalidOperationException("A sample operation is already running.");
    }

    private void Exit() => Volatile.Write(ref _inFlight, 0);
}
