using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Restrictive private-run gate. It supplies no credential or spending authority.</summary>
public sealed class CompletionWorkflowGate
{
    private readonly Func<CompletionGateConfiguration?> _load;
    private readonly ICompletionGateCommandRunner _runner;
    private readonly ILogger<CompletionWorkflowGate>? _logger;
    private readonly AsyncLocal<CompletionRefreshLease?> _current = new();
    private int _requiredObserved;

    public CompletionWorkflowGate(AppPaths paths, ILogger<CompletionWorkflowGate>? logger = null) : this(() => LoadInstalled(paths.RootDirectory, AppContext.BaseDirectory), new CompletionGateCommandRunner(), logger) { }

    public CompletionWorkflowGate(Func<CompletionGateConfiguration?> load, ICompletionGateCommandRunner runner, ILogger<CompletionWorkflowGate>? logger = null)
    { _load = load; _runner = runner; _logger = logger; }

    private CompletionGateConfiguration? Load()
    {
        try
        {
            var configuration = _load();
            if (configuration is not null) Volatile.Write(ref _requiredObserved, 1);
            else if (Volatile.Read(ref _requiredObserved) != 0) throw new CompletionGateException(CompletionGateFailure.PinMismatch);
            return configuration;
        }
        catch (CompletionGateException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new CompletionGateException(CompletionGateFailure.Storage); }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new CompletionGateException(CompletionGateFailure.PinMismatch); }
    }

    public bool IsRequired => Load() is not null;

    public void RequireReady()
    {
        var current = Load();
        if (current is not null) _runner.Run(current, "status", Guid.Empty, "", null);
    }

    public Guid CurrentRefreshId(Guid fallback) => _current.Value?.Id ?? fallback;

    public void BeforeRequest(bool probe)
    {
        if (Load() is null) return;
        (_current.Value ?? throw new InvalidOperationException("Completion request has no owned refresh.")).Request(probe);
    }

    public void BeforeCredential(string reference)
    {
        var current = Load();
        if (current is null) return;
        if (!string.Equals(reference, current.CredentialReferenceId, StringComparison.Ordinal)) throw new InvalidOperationException("Completion credential binding changed.");
        if (_current.Value is { } lease) lease.CheckBeforeEffect();
        else _runner.Run(current, "status", Guid.Empty, "", null);
    }

    public void BeforeSynthesis(string reference, Uri endpoint)
    {
        var current = Load();
        if (current is null) return;
        if (_current.Value is not null || !string.Equals(endpoint.AbsoluteUri, current.SynthesisEndpoint, StringComparison.Ordinal))
            throw new CompletionGateException(CompletionGateFailure.PinMismatch);
        BeforeCredential(reference); // Existing per-request financial/identity guards remain mandatory.
    }

    public CompletionRefreshScope BeginRefresh(Guid id)
    {
        var configuration = Load();
        if (configuration is null) return new(null, false, null);
        if (_current.Value is { } existing)
        {
            existing.Require(this, configuration, id);
            return new(existing, false, null);
        }
        string identity = Hash(Encoding.UTF8.GetBytes(configuration.Pin + "|refresh|" + id.ToString("D")));
        _runner.Run(configuration, "claim-refresh", id, identity, null);
        var lease = new CompletionRefreshLease(this, configuration, id, identity);
        _current.Value = lease;
        return new(lease, true, () => _current.Value = null);
    }

    public void RequireOutsideRun()
    {
        if (Load() is not null) throw new InvalidOperationException("Completion run blocks unrelated credential/configuration operations.");
    }

    public void BeforeNetwork(ProviderAccountReference account, Uri endpoint, bool probe, Guid? refreshId = null)
    {
        var configuration = Load();
        if (configuration is null) return;
        var lease = _current.Value ?? throw new InvalidOperationException("Completion run requires an owned catalog refresh.");
        lease.Require(this, configuration, refreshId);
        if (!string.Equals(account.AccountId, configuration.AccountId, StringComparison.Ordinal)
            || !string.Equals(account.CredentialReference?.TargetName, configuration.CredentialReferenceId, StringComparison.Ordinal)
            || !string.Equals(endpoint.AbsoluteUri, probe ? configuration.SynthesisEndpoint : configuration.CatalogEndpoint, StringComparison.Ordinal))
            throw new InvalidOperationException("Completion refresh target changed.");
        lease.CheckBeforeEffect();
    }

    public void RequireProject(string project)
    {
        var configuration = Load();
        if (configuration is not null && !string.Equals(project, configuration.ProjectId, StringComparison.Ordinal))
            throw new InvalidOperationException("Completion project binding changed.");
    }

    public void RequireAccount(ProviderAccountReference account)
    {
        var configuration = Load();
        if (configuration is not null && (!string.Equals(account.AccountId, configuration.AccountId, StringComparison.Ordinal)
            || !string.Equals(account.CredentialReference?.TargetName, configuration.CredentialReferenceId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Completion account/credential binding changed.");
        if (configuration is not null) BeforeCredential(configuration.CredentialReferenceId);
    }

    public void CatalogObserved(GoogleVoiceCatalogSnapshot snapshot)
    {
        if (Load() is null) return;
        var lease = _current.Value ?? throw new InvalidOperationException("Catalog observation has no owned refresh.");
        lease.CatalogObserved(snapshot);
    }

    public void ProbeObserved(string project, GoogleSynthesisCapabilityEvidence evidence)
    {
        if (Load() is null) return;
        var lease = _current.Value ?? throw new InvalidOperationException("Probe observation has no owned refresh.");
        lease.ProbeObserved(project, evidence);
    }

    public void PersistenceObserved(GoogleTextToSpeechCatalogBootstrapResult result)
    {
        if (Load() is null) return;
        (_current.Value ?? throw new InvalidOperationException("Persistence has no owned refresh.")).PersistenceObserved(result);
    }

    internal void Check(CompletionRefreshLease lease)
    {
        var current = Load() ?? throw new InvalidOperationException("Required completion gate disappeared.");
        lease.Require(this, current, null);
        _runner.Run(current, "check", lease.Id, lease.Identity, null);
    }

    internal void Finish(CompletionRefreshLease lease, bool succeeded)
    {
        try
        {
            var current = Load() ?? throw new InvalidOperationException("Required completion gate disappeared.");
            lease.Require(this, current, null);
            var receipt = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = lease.Id.ToString("D"),
                ["identitySha256"] = lease.Identity,
                ["outcome"] = succeeded ? "Succeeded" : "Failed",
                ["evidenceSha256"] = lease.EvidenceHash,
                ["catalogVerified"] = succeeded,
                ["probeInputEmpty"] = succeeded,
                ["probeHttpStatus"] = succeeded ? 400 : 0,
                ["probeStatus"] = succeeded ? "INVALID_ARGUMENT" : "",
                ["audioAbsent"] = succeeded,
            };
            _runner.Run(current, "complete", lease.Id, lease.Identity, receipt);
        }
        catch (Exception error) when (!succeeded && GoogleCatalogRefreshDiagnostics.IsNonFatal(error))
        {
            // A failed operation stays failed/pending. Cleanup must not replace its
            // original HTTP/storage exception or cause a retry.
            GoogleCatalogRefreshDiagnostics.Failure(_logger, lease.Id, GoogleCatalogRefreshPhase.UiBootstrap,
                new CompletionGateException(error is CompletionGateException gate ? gate.Reason : CompletionGateFailure.Storage));
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static CompletionGateConfiguration? LoadInstalled(string profile, string app)
    {
        string install = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(app)))!;
        string markerPath = Path.Combine(install, "cloudscribe-install.json");
        string requiredPath = Path.Combine(profile, ".completion-workflow-required.json");
        bool required = File.Exists(requiredPath);
        JsonDocument? marker = null;
        try
        {
            if (File.Exists(markerPath)) marker = JsonDocument.Parse(Read(markerPath, 32768));
            bool pinned = marker?.RootElement.TryGetProperty("CompletionWorkflowGateSha256", out _) == true;
            if (!required && !pinned) return null;
            if (!required || !pinned) throw new InvalidDataException("Completion gate activation is incomplete.");
            using var requirement = JsonDocument.Parse(Read(requiredPath, 4096));
            ChirpPolicyFiles.RequireUnique(requirement.RootElement);
            ChirpPolicyFiles.RequireUnique(marker!.RootElement);
            if (!string.Equals(marker.RootElement.GetProperty("Product").GetString(), "CloudScribe Pro", StringComparison.Ordinal)
                || marker.RootElement.GetProperty("CompletionGateProtocolVersion").GetInt32() != 1
                || requirement.RootElement.EnumerateObject().Count() != 2)
                throw new InvalidDataException("Completion gate activation identity changed.");
            string pin = marker.RootElement.GetProperty("CompletionWorkflowGateSha256").GetString()!;
            if (!ChirpPolicyValidation.IsHash(pin) || !string.Equals(requirement.RootElement.GetProperty("GateSha256").GetString(), pin, StringComparison.Ordinal))
                throw new InvalidDataException("Completion gate requirement does not match its installer pin.");
            string path = Path.Combine(install, "completion-workflow-gate.json");
            byte[] bytes = Read(path, 32768);
            if (!string.Equals(Hash(bytes), pin, StringComparison.Ordinal)) throw new InvalidDataException("Completion gate bytes changed.");
            var configuration = ChirpPolicyFiles.Parse<CompletionGateConfiguration>(bytes) with { Pin = pin, ConfigPath = path };
            if (!string.Equals(requirement.RootElement.GetProperty("ScopeSha256").GetString(), configuration.ScopeSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Completion gate parent scope changed.");
            if (configuration.SchemaVersion != 1 || !string.Equals(configuration.ProfileRootSha256, InstalledRecoveryAdmissionStore.ProfileHash(profile), StringComparison.Ordinal)
                || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration.ApplicationDirectory)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(app)), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(Read(Path.Combine(app, "CloudScribe.dll"), 512 * 1024 * 1024)), configuration.AppSha256, StringComparison.Ordinal)
                || !string.Equals(Hash(Read(Path.Combine(app, "CloudScribe.Infrastructure.dll"), 512 * 1024 * 1024)), configuration.InfrastructureSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Completion gate build/profile binding changed.");
            return configuration;
        }
        finally { marker?.Dispose(); }
    }

    internal static byte[] Read(string path, int maximum)
    {
        for (string? at = Path.GetFullPath(path); at is not null; at = Path.GetDirectoryName(at))
            if ((File.Exists(at) || Directory.Exists(at)) && File.GetAttributes(at).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Linked completion gate material refused.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("Completion gate material exceeds its bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Completion material changed while reading.");
        return bytes;
    }
}
