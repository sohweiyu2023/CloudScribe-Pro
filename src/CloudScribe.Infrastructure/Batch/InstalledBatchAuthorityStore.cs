using System.Text.Json;
using System.Text.Json.Serialization;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Reads the batch profile and reviewed consents from the install folder (<c>&lt;install&gt;\batch-authority\</c>).
/// This is the same trust boundary as the existing reviewed article policies: the app never writes there; only the
/// reviewed <c>Install-BatchAuthority.ps1</c> does, and it records each file's SHA-256 in <c>pins.json</c>, so an
/// edited or partly written file is ignored. In-app consents live in the user profile and are accepted only as
/// <see cref="BatchConsentSource.InApp"/>, so a copied file can never pose as a reviewed approval.
/// Any read or validation failure means "not configured".</summary>
public sealed class InstalledBatchAuthorityStore(string applicationDirectory, string profileRoot) : IBatchAuthorityStore
{
    private const int MaximumProfileBytes = 64 * 1024;
    private const int MaximumConsentBytes = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string InstalledDirectory
    {
        get
        {
            string app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
            return Path.Combine(Path.GetDirectoryName(app) ?? app, "batch-authority");
        }
    }

    private string InAppDirectory => Path.Combine(Path.GetFullPath(profileRoot), "batches", "consents");

    public async Task<BatchAuthorityProfile?> LoadProfileAsync(CancellationToken cancellationToken = default)
    {
        var pins = await TryReadAsync<BatchAuthorityPins>(Path.Combine(InstalledDirectory, "pins.json"), MaximumProfileBytes, null, cancellationToken).ConfigureAwait(false);
        if (pins is null) return null;
        var profile = await TryReadAsync<BatchAuthorityProfile>(Path.Combine(InstalledDirectory, "profile.json"), MaximumProfileBytes, pins.ProfileSha256, cancellationToken).ConfigureAwait(false);
        if (profile is null || profile.Problem() is not null
            || !string.Equals(profile.ProfileRootSha256, InstalledRecoveryAdmissionStore.ProfileHash(profileRoot), StringComparison.Ordinal)) return null;
        return profile;
    }

    public async Task<BatchConsent?> LoadConsentAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        string name = batchId.ToString("N") + ".json";
        var pins = await TryReadAsync<BatchAuthorityPins>(Path.Combine(InstalledDirectory, "pins.json"), MaximumProfileBytes, null, cancellationToken).ConfigureAwait(false);
        string? pin = pins?.Consents?.GetValueOrDefault(batchId.ToString("N"));
        var reviewed = pin is null ? null
            : await TryReadAsync<BatchConsent>(Path.Combine(InstalledDirectory, "consents", name), MaximumConsentBytes, pin, cancellationToken).ConfigureAwait(false);
        if (reviewed is { Source: BatchConsentSource.ReviewedInstall } && reviewed.BatchId == batchId) return reviewed;
        BatchConsent? newest = null;
        foreach (var inApp in await ReadInAppAsync(cancellationToken).ConfigureAwait(false))
            if (inApp.BatchId == batchId && (newest is null || inApp.CreatedUtc > newest.CreatedUtc)) newest = inApp;
        return newest;
    }

    /// <summary>Every readable approval: pinned reviewed ones and in-app ones (each still marked with its source).</summary>
    public async Task<IReadOnlyList<BatchConsent>> ListConsentsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<BatchConsent>();
        var pins = await TryReadAsync<BatchAuthorityPins>(Path.Combine(InstalledDirectory, "pins.json"), MaximumProfileBytes, null, cancellationToken).ConfigureAwait(false);
        foreach (var (key, pin) in pins?.Consents ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!Guid.TryParseExact(key, "N", out var id)) continue;
            var reviewed = await TryReadAsync<BatchConsent>(Path.Combine(InstalledDirectory, "consents", key + ".json"), MaximumConsentBytes, pin, cancellationToken).ConfigureAwait(false);
            if (reviewed is { Source: BatchConsentSource.ReviewedInstall } && reviewed.BatchId == id) result.Add(reviewed);
        }
        result.AddRange(await ReadInAppAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    /// <summary>In-app approvals are stored one file per approval (<c>&lt;batch&gt;-&lt;approval&gt;.json</c>) and never
    /// overwritten, so a job started under an earlier approval always keeps its admission.</summary>
    private async Task<List<BatchConsent>> ReadInAppAsync(CancellationToken cancellationToken)
    {
        var result = new List<BatchConsent>();
        if (!Directory.Exists(InAppDirectory)) return result;
        foreach (string file in Directory.EnumerateFiles(InAppDirectory, "*.json"))
        {
            string[] parts = Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var batch) || !Guid.TryParseExact(parts[1], "N", out var consent)) continue;
            var inApp = await TryReadAsync<BatchConsent>(file, MaximumConsentBytes, null, cancellationToken).ConfigureAwait(false);
            if (inApp is { Source: BatchConsentSource.InApp } && inApp.BatchId == batch && inApp.ConsentId == consent) result.Add(inApp);
        }
        return result;
    }

    public async Task SaveInAppConsentAsync(BatchConsent consent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (consent.Source != BatchConsentSource.InApp) throw new InvalidOperationException("Only an in-app approval can be saved by the app.");
        Directory.CreateDirectory(InAppDirectory);
        string path = Path.Combine(InAppDirectory, consent.BatchId.ToString("N") + "-" + consent.ConsentId.ToString("N") + ".json");
        if (File.Exists(path)) throw new InvalidOperationException("An approval is never overwritten.");
        string temporary = path + ".tmp";
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, consent, Json, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, path, overwrite: false);
    }

    private static async Task<T?> TryReadAsync<T>(string path, int maximumBytes, string? pinnedSha256, CancellationToken token) where T : class
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > maximumBytes) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (bytes.Length > maximumBytes) return null;
            if (pinnedSha256 is not null && !string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), pinnedSha256, StringComparison.OrdinalIgnoreCase)) return null;
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        { return null; } // No path or file content reaches logs.
    }
}
