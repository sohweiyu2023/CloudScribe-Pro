using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only installation bridge. A missing pin or ledger never initializes or resets history.</summary>
public sealed class InstalledGoogleLongAudioStore(string applicationDirectory, AppPaths paths, TimeProvider clock)
{
    public async Task<InstalledGoogleLongAudioContext?> LoadVerifiedAsync(CancellationToken token = default) =>
        (await ReadInstallationAsync(token).ConfigureAwait(false)).Context;

    public async Task<GoogleLongAudioInstallationRead> ReadInstallationAsync(CancellationToken token = default)
    {
        var status = GoogleLongAudioInstallationStatus.Unconfigured;
        try
        {
            string app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
            string install = Path.GetDirectoryName(app) ?? throw new InvalidDataException("Installation identity is absent.");
            string markerPath = Path.Combine(install, "cloudscribe-install.json");
            if (!File.Exists(markerPath)) return new(null, status, null);
            status = GoogleLongAudioInstallationStatus.IdentityMismatch;
            byte[] markerBytes = await ChirpPolicyFiles.ReadAsync(markerPath, 16384, token).ConfigureAwait(false);
            using var marker = JsonDocument.Parse(markerBytes); ChirpPolicyFiles.RequireUnique(marker.RootElement);
            if (marker.RootElement.TryGetProperty("LongAudioBuildRebindPending", out _)) return new(null, GoogleLongAudioInstallationStatus.PendingProvisioning, null);
            if (!marker.RootElement.TryGetProperty("GoogleLongAudioScopeSha256", out var pin)) return new(null, GoogleLongAudioInstallationStatus.Unconfigured, null);
            status = GoogleLongAudioInstallationStatus.ScopeMismatch;
            if (!ChirpPolicyValidation.IsHash(pin.GetString())) return new(null, status, null);
            status = GoogleLongAudioInstallationStatus.PendingProvisioning;
            string leasePath = Path.Combine(install, "long-audio-provisioning.lock"); ChirpPolicyFiles.RejectLinks(leasePath);
            using var installLease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var context = await ReadBoundContextAsync(app, install, marker.RootElement, markerBytes, value => status = value, token).ConfigureAwait(false);
            return new(context, context is null ? status : GoogleLongAudioInstallationStatus.Ready, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or NullReferenceException or OverflowException)
        { return new(null, status, error.HResult); }
    }

    private async Task<InstalledGoogleLongAudioContext?> ReadBoundContextAsync(string app, string install, JsonElement marker,
        byte[] markerBytes, Action<GoogleLongAudioInstallationStatus> observe, CancellationToken token)
    {
        observe(GoogleLongAudioInstallationStatus.IdentityMismatch);
        var parent = await new InstalledChirpArticleAdmissionStore(app, paths, clock).LoadVerifiedAsync(token).ConfigureAwait(false);
        if (parent is null) return null;
        string parentPath = Path.Combine(install, "chirp-article-admission-policy.json"), scopePath = Path.Combine(install, "google-long-audio-scope.json");
        string parentSha = ChirpPolicyFiles.Hash(await ChirpPolicyFiles.ReadAsync(parentPath, 8 * 1024 * 1024, token).ConfigureAwait(false));
        if (!Same(parentSha, marker.GetProperty("ChirpArticlePolicySha256").GetString())) return null;
        observe(GoogleLongAudioInstallationStatus.ScopeMismatch);
        byte[] scopeBytes = await ChirpPolicyFiles.ReadAsync(scopePath, 8 * 1024 * 1024, token).ConfigureAwait(false);
        string scopeSha = ChirpPolicyFiles.Hash(scopeBytes);
        if (!Same(scopeSha, marker.GetProperty("GoogleLongAudioScopeSha256").GetString())) return null;
        var scope = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetScope>(scopeBytes);
        if (scope.Version != 1 || !Same(scope.ParentPolicySha256, parentSha)) return null;
        GoogleLongAudioFailedRangeReplacementPlanner.RequireScopeShape(scope);
        GoogleLongAudioDeferredRange.RequireScope(scope);
        observe(GoogleLongAudioInstallationStatus.PendingProvisioning);
        if (!await RequireProofAsync(install, markerBytes, scopeSha, parentSha, parent, token).ConfigureAwait(false)) return null;
        observe(GoogleLongAudioInstallationStatus.LedgerUnavailable);
        string root = Path.Combine(paths.RootDirectory, "data", "google-long-audio"), ledgerPath = Path.Combine(root, "budget-ledger.json");
        var ledger = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetLedger>(await ChirpPolicyFiles.ReadAsync(ledgerPath, 8 * 1024 * 1024, token).ConfigureAwait(false));
        if (ledger.Version != 1 || !Same(ledger.ScopeSha256, scopeSha) || ledger.Claims is null) return null;
        string operations = Path.Combine(root, "operations"), output = Path.Combine(root, "output");
        ChirpPolicyFiles.RejectLinks(operations); ChirpPolicyFiles.RejectLinks(output);
        return new(parentPath, parentSha, scopePath, scopeSha, ledgerPath, operations, output, scope);
    }

    private static async Task<bool> RequireProofAsync(string install, byte[] markerBytes, string scopeSha, string parentSha,
        ChirpArticleAdmissionPolicy parent, CancellationToken token)
    {
        string directory = Path.Combine(install, "long-audio-provisioning");
        byte[] planBytes = await ChirpPolicyFiles.ReadAsync(Path.Combine(directory, "plan.json"), 16384, token).ConfigureAwait(false);
        var plan = ChirpPolicyFiles.Parse<GoogleLongAudioProvisioningPlan>(planBytes);
        var receipt = ChirpPolicyFiles.Parse<GoogleLongAudioProvisioningReceipt>(await ChirpPolicyFiles.ReadAsync(Path.Combine(directory, "receipt.json"), 16384, token).ConfigureAwait(false));
        return plan.Version == 1 && plan.TransactionId != Guid.Empty && receipt.Version == 1 && receipt.TransactionId == plan.TransactionId
            && Same(receipt.Phase, "Complete") && Same(receipt.PlanSha256, ChirpPolicyFiles.Hash(planBytes))
            && Same(plan.ScopeSha256, scopeSha) && Same(plan.ParentPolicySha256, parentSha) && Same(plan.ProfileRootSha256, parent.ProfileRootSha256)
            && Same(plan.AppSha256, parent.AppSha256) && Same(plan.InfrastructureSha256, parent.InfrastructureSha256)
            && Same(plan.NewMarkerSha256, ChirpPolicyFiles.Hash(markerBytes)) && ChirpPolicyValidation.IsHash(plan.PreviousMarkerSha256)
            && ChirpPolicyValidation.IsHash(plan.InitialLedgerSha256) && plan.CreatedUtc.Offset == TimeSpan.Zero && plan.CreatedUtc != default;
    }
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
}
