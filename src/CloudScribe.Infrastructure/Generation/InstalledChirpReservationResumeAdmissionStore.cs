using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Current installer pin, separate from expired historical article authority. Runtime JSON cannot admit itself.</summary>
public sealed class InstalledChirpReservationResumeAdmissionStore(string applicationDirectory, AppPaths paths, TimeProvider clock,
    ILogger<InstalledChirpReservationResumeAdmissionStore>? logger = null)
    : IChirpReservationResumeAdmissionStore
{
    private string? _lastRejection;
    public async Task<ChirpReservationResumePolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default)
    {
        bool hasPin = false; LocalRequestReason readFailure = LocalRequestReason.ResumePinMismatch;
        ChirpReservationResumePolicy? inspected = null;
        try
        {
            string app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
            string install = Path.GetDirectoryName(app) ?? throw new InvalidDataException("Installation identity is absent.");
            using var marker = JsonDocument.Parse(await ChirpPolicyFiles.ReadAsync(Path.Combine(install, "cloudscribe-install.json"), 16384, cancellationToken).ConfigureAwait(false));
            ChirpPolicyFiles.RequireUnique(marker.RootElement);
            if (!marker.RootElement.TryGetProperty("ChirpReservationResumePolicySha256", out var pin)) return null;
            hasPin = true;
            if (!string.Equals(marker.RootElement.GetProperty("Product").GetString(), "CloudScribe Pro", StringComparison.Ordinal)
                || !ChirpPolicyValidation.IsHash(pin.GetString())) return Rejected(LocalRequestReason.ResumePinMismatch);
            byte[] bytes = await ChirpPolicyFiles.ReadAsync(Path.Combine(install, "chirp-reservation-resume-admission.json"), 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(pin.GetString(), ChirpPolicyFiles.Hash(bytes), StringComparison.Ordinal)) return Rejected(LocalRequestReason.ResumePinMismatch);
            var policy = ChirpPolicyFiles.Parse<ChirpReservationResumePolicy>(bytes);
            inspected = policy;
            if (policy.ExpiresAtUtc <= clock.GetUtcNow() || policy.CatalogExpiresAtUtc <= clock.GetUtcNow()) return Rejected(LocalRequestReason.ResumeExpired, policy);
            ChirpReservationResumeValidation.RequireValid(policy, clock.GetUtcNow());
            readFailure = LocalRequestReason.ResumeBuildMismatch;
            string appHash = ChirpPolicyFiles.Hash(await ChirpPolicyFiles.ReadAsync(Path.Combine(app, "CloudScribe.dll"), 512 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
            string infrastructureHash = ChirpPolicyFiles.Hash(await ChirpPolicyFiles.ReadAsync(Path.Combine(app, "CloudScribe.Infrastructure.dll"), 512 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(policy.AppSha256, appHash, StringComparison.Ordinal)
                || !string.Equals(marker.RootElement.GetProperty("AppSha256").GetString(), appHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(policy.InfrastructureSha256, infrastructureHash, StringComparison.Ordinal)) return Rejected(LocalRequestReason.ResumeBuildMismatch, policy);
            if (!string.Equals(policy.ProfileRootSha256, InstalledRecoveryAdmissionStore.ProfileHash(paths.RootDirectory), StringComparison.Ordinal)) return Rejected(LocalRequestReason.ResumeProfileMismatch, policy);
            Interlocked.Exchange(ref _lastRejection, null);
            return policy;
        }
        catch (FileNotFoundException) { return hasPin ? Rejected(readFailure, inspected) : null; }
        catch (UnauthorizedAccessException) { return Rejected(LocalRequestReason.AccessDenied, inspected); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or NullReferenceException or OverflowException)
        { return Rejected(error is IOException ? LocalRequestReason.Storage : LocalRequestReason.ResumePolicyMalformed, inspected); }
    }

    private ChirpReservationResumePolicy? Rejected(LocalRequestReason reason, ChirpReservationResumePolicy? policy = null)
    {
        string stamp = reason + "/" + policy?.ExpiresAtUtc.UtcTicks;
        if (!string.Equals(Interlocked.Exchange(ref _lastRejection, stamp), stamp, StringComparison.Ordinal))
            _ = LocalRequestDiagnostics.Failure(logger, Guid.NewGuid(), LocalRequestStage.ResumeAdmission,
                LocalRequestDiagnostics.Mark(new InvalidOperationException("Resume admission verification rejected fixed safe metadata."), reason,
                    admissionExpiresUtc: policy?.ExpiresAtUtc, capabilityExpiresUtc: policy?.CatalogExpiresAtUtc), clock.GetUtcNow());
        return null; // Logging failure cannot manufacture authority or break ordinary library readback.
    }
}
