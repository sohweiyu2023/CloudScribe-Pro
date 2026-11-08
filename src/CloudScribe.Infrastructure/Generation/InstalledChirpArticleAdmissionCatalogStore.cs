using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only sidecar authority. No marker, grant, receipt, profile or ledger is written by this reader.</summary>
public sealed partial class InstalledChirpArticleAdmissionCatalogStore(string applicationDirectory, AppPaths paths, TimeProvider clock)
    : IChirpArticleAdmissionCatalog
{
    private readonly InstalledChirpArticleAdmissionStore _legacy = new(applicationDirectory, paths, clock);
    private string? _observedRegistry;

    public async Task<ChirpArticleAdmissionPolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default)
    {
        var legacy = (await LoadCatalogVerifiedAsync(cancellationToken).ConfigureAwait(false)).LegacyPolicy;
        return legacy is not null && (ChirpPolicyValidation.IsCompletionBound(legacy) || legacy.ExpiresAtUtc > clock.GetUtcNow()) ? legacy : null;
    }

    public async Task<ChirpArticleAdmissionCatalog> LoadCatalogVerifiedAsync(CancellationToken cancellationToken = default)
    {
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException or NullReferenceException or OverflowException or FormatException)
        { throw CatalogRejected(); } // Raw exceptions can contain private file paths and policy content.
    }

    private async Task<ChirpArticleAdmissionCatalog> LoadCoreAsync(CancellationToken token)
    {
        string app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        string install = Path.GetDirectoryName(app) ?? throw new InvalidDataException("Installation identity is absent.");
        string root = Path.Combine(install, "chirp-article-admissions"), registryPath = Path.Combine(root, "registry.json");
        string? observed = Volatile.Read(ref _observedRegistry);
        ChirpPolicyFiles.RejectLinks(registryPath);
        if (!File.Exists(registryPath))
        {
            await RequireUnpublishedAsync(root, observed, token).ConfigureAwait(false);
            return new(await _legacy.LoadVerifiedAsync(token).ConfigureAwait(false), []); // Legacy validation also rejects orphaned additive SQL claims after restart.
        }
        var legacy = await _legacy.LoadRetainedVerifiedAsync(token).ConfigureAwait(false);
        byte[] bytes = await ChirpPolicyFiles.ReadAsync(registryPath, 8 * 1024 * 1024, token).ConfigureAwait(false);
        var registry = ChirpPolicyFiles.Parse<Registry>(bytes);
        string registryHash = ChirpPolicyFiles.Hash(bytes);
        var binding = await VerifyHistoryAsync(root, registry, registryHash, observed, token).ConfigureAwait(false);
        if (legacy is null || !string.Equals(registry.LegacyPolicySha256, ChirpPolicyValidation.Hash(legacy), StringComparison.Ordinal))
            throw new InvalidDataException("The retained legacy authority changed outside its admitted build binding.");
        byte[] marker = await ChirpPolicyFiles.ReadAsync(Path.Combine(install, "cloudscribe-install.json"), 16384, token).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(marker))
        {
            ChirpPolicyFiles.RequireUnique(document.RootElement);
            if (document.RootElement.TryGetProperty("LongAudioBuildRebindPending", out _)) throw new InvalidDataException("A build transition is incomplete.");
            if (string.Equals(binding.Operation, "Rebind", StringComparison.Ordinal))
            {
                using var plan = JsonDocument.Parse(await ReadPinnedAsync(root, "plans", binding.PlanSha256, token).ConfigureAwait(false));
                RequireString(plan.RootElement, "NewParentSha256", document.RootElement.GetProperty("ChirpArticlePolicySha256").GetString());
                RequireString(plan.RootElement, "NewScopeSha256", document.RootElement.GetProperty("GoogleLongAudioScopeSha256").GetString());
            }
        }
        if (!string.Equals(binding.InstallMarkerSha256, ChirpPolicyFiles.Hash(marker), StringComparison.Ordinal)
            || !string.Equals(binding.ProfileRootSha256, InstalledRecoveryAdmissionStore.ProfileHash(paths.RootDirectory), StringComparison.Ordinal)
            || !string.Equals(binding.AppSha256, ChirpPolicyFiles.Hash(await ChirpPolicyFiles.ReadAsync(Path.Combine(app, "CloudScribe.dll"), 512 * 1024 * 1024, token).ConfigureAwait(false)), StringComparison.Ordinal)
            || !string.Equals(binding.InfrastructureSha256, ChirpPolicyFiles.Hash(await ChirpPolicyFiles.ReadAsync(Path.Combine(app, "CloudScribe.Infrastructure.dll"), 512 * 1024 * 1024, token).ConfigureAwait(false)), StringComparison.Ordinal))
            throw new InvalidDataException("The additional catalog is not bound to this exact installed build and profile.");
        List<ChirpAdditionalAdmissionFamily> families = [];
        foreach (var entry in registry.Families)
            families.Add(await ReadFamilyAsync(root, entry, binding, token).ConfigureAwait(false));
        var catalog = new ChirpArticleAdmissionCatalog(legacy, families.AsReadOnly());
        ChirpAdditionalAdmissionValidation.RequireValid(catalog, clock.GetUtcNow());
        byte[] after = await ChirpPolicyFiles.ReadAsync(registryPath, 8 * 1024 * 1024, token).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(after)) throw new InvalidDataException("Admission changed while verifying its complete registry.");
        string? concurrent = Interlocked.CompareExchange(ref _observedRegistry, registryHash, observed);
        if (!string.Equals(concurrent, observed, StringComparison.Ordinal) && !string.Equals(concurrent, registryHash, StringComparison.Ordinal))
            throw new InvalidDataException("Concurrent admission observation changed; recheck the complete current catalog.");
        return catalog;
    }

    private static async Task<byte[]> ReadPinnedAsync(string root, string folder, string hash, CancellationToken token, string extension = "json")
    {
        if (!ChirpPolicyValidation.IsHash(hash)) throw new InvalidDataException("An immutable admission pin is invalid.");
        var bytes = await ChirpPolicyFiles.ReadAsync(Path.Combine(root, folder, hash + "." + extension), 8 * 1024 * 1024, token).ConfigureAwait(false);
        if (!string.Equals(hash, ChirpPolicyFiles.Hash(bytes), StringComparison.Ordinal)) throw new InvalidDataException("Immutable admission evidence changed.");
        return bytes;
    }

    private sealed record Registry(int SchemaVersion, string LegacyPolicySha256, IReadOnlyList<Entry> Families, string BindingReceiptSha256);
    private sealed record Entry(Guid FamilyId, string GrantSha256, string ReceiptSha256);
    private sealed record Receipt(int SchemaVersion, Guid TransactionId, string Operation, string PlanSha256, string? PreviousRegistrySha256,
        Guid? FamilyId, string? GrantSha256, string ProfileRootSha256, string AppSha256, string InfrastructureSha256, string InstallMarkerSha256,
        string LegacyPolicySha256, string SourceApprovalSha256, string NativeProofSha256, string Phase);

    private static InvalidOperationException CatalogRejected() => CloudScribe.Application.Logging.LocalRequestDiagnostics.Mark(
        new InvalidOperationException("The independent article admission catalog is missing, changed or incomplete."),
        CloudScribe.Application.Logging.LocalRequestReason.AdmissionChanged);
}
