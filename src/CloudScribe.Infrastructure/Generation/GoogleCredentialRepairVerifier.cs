using System.Security.Cryptography;
using CloudScribe.Application.Providers;
using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit candidate verification using memory-only material, OAuth/catalog and an input-free capability probe.</summary>
public sealed class GoogleCredentialRepairVerifier(HttpClient httpClient, TimeProvider clock, ILoggerFactory? loggerFactory = null) : IDisposable
{
    internal static readonly Uri Catalog = new("https://texttospeech.googleapis.com/v1/voices");
    internal static readonly Uri Synthesis = new("https://texttospeech.googleapis.com/v1/text:synthesize");

    public async Task<GoogleVerifiedRepairCandidate> VerifyAsync(ReadOnlyMemory<char> json, ProviderAccountSnapshot account,
        string expectedProject, string expectedEmail, string reference, CancellationToken token = default)
    {
        RequireBinding(account);
        if (json.IsEmpty || json.Length > 16 * 1024) throw new InvalidDataException("Candidate file exceeds its reviewed limit.");
        var memory = new GoogleRepairMemoryVault();
        try
        {
            var importer = new GoogleServiceAccountCredentialOnboardingService(memory);
            var identity = await importer.ImportAsync(reference, json, Catalog, Synthesis, token).ConfigureAwait(false);
            RequireSafeIdentity(identity);
            if (!string.Equals(identity.ProjectId, expectedProject, StringComparison.Ordinal)
                || !string.Equals(identity.ClientEmail, expectedEmail, StringComparison.Ordinal)
                || !identity.ClientEmail.EndsWith("@" + expectedProject + ".iam.gserviceaccount.com", StringComparison.Ordinal))
                throw new InvalidOperationException("Candidate identity does not match the intended existing service account and project.");
            string publicKey = await PublicKeyAsync(memory, reference, token).ConfigureAwait(false);
            var admitted = new ProviderAccountReference(account.Reference.ProviderStableId, account.Reference.AccountId,
                account.Reference.DisplayName, new CredentialReference(reference), account.Reference.EndpointId,
                account.Reference.RegionId, account.Reference.EndpointOrigin);
            using var resolver = new VaultBackedTransientCredentialResolver(memory, httpClient, clock, SafeLogger<VaultBackedTransientCredentialResolver>());
            _ = await new GoogleVoiceCatalogClient(httpClient, resolver, SafeLogger<GoogleVoiceCatalogClient>()).LoadAsync(admitted, Catalog, token).ConfigureAwait(false);
            _ = await new GoogleSynthesisCapabilityProbe(httpClient, resolver, clock, SafeLogger<GoogleSynthesisCapabilityProbe>()).VerifyAsync(admitted, expectedProject, Synthesis, token).ConfigureAwait(false);
            return new(new(identity.ProjectId, identity.ClientEmail, identity.PrivateKeyId, publicKey), memory, reference);
        }
        catch { memory.Dispose(); throw; }
    }

    private static void RequireSafeIdentity(GoogleServiceAccountCredentialIdentity identity)
    {
        if (!SafeField(identity.ProjectId, @"\A[a-z][a-z0-9-]{4,28}[a-z0-9]\z")
            || !SafeField(identity.ClientEmail, @"\A[a-z0-9][a-z0-9.-]{0,62}@[a-z][a-z0-9-]{4,28}[a-z0-9]\.iam\.gserviceaccount\.com\z")
            || !SafeField(identity.PrivateKeyId, @"\A[0-9a-f]{40}\z"))
            throw new InvalidDataException("Candidate identity fields have an unsupported shape; no secret-bearing value is displayed or retained.");
    }

    private static bool SafeField(string value, string pattern) => value.Length <= 254
        && System.Text.RegularExpressions.Regex.IsMatch(value, pattern,
            System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static async Task<string> PublicKeyAsync(GoogleRepairMemoryVault memory, string reference, CancellationToken token)
    {
        using CredentialSecret secret = await memory.ReadAsync(new CredentialReference(reference + GoogleServiceAccountCredentialOnboardingService.PrivateKeySuffix), token)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Candidate key is unavailable.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(secret.Value.Span);
        return Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
    }

    internal static void RequireBinding(ProviderAccountSnapshot account)
    {
        if (!account.IsEnabled || !string.Equals(account.Reference.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal)
            || account.Reference.CredentialReference is null || !string.Equals(account.Reference.RegionId, "global", StringComparison.Ordinal)
            || !string.Equals(account.Reference.EndpointId, GoogleTextToSpeechEndpointIdentity.Create(Synthesis), StringComparison.Ordinal)
            || account.Reference.EndpointOrigin != new Uri(Synthesis.GetLeftPart(UriPartial.Authority)))
            throw new InvalidOperationException("This repair supports only the existing admitted global Google endpoint binding.");
    }

    private ILogger<T>? SafeLogger<T>()
    {
        try { return loggerFactory?.CreateLogger<T>(); }
        catch (Exception error) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(error)) { return null; }
    }

    public void Dispose() => httpClient.Dispose();
}
