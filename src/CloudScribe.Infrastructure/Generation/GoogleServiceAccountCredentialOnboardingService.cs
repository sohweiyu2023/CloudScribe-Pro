using System.Text.Json;
using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Imports a Google service-account JSON credential into Windows-backed secret storage without
/// persisting the source JSON or an OAuth bearer token. The private key and non-secret identity
/// metadata are stored as separate credential entries so the private key remains below the
/// Windows Credential Manager blob limit. The exact user-supplied catalog and synthesis endpoints
/// are persisted with the credential binding so later requests reuse only previously admitted paths.
/// </summary>
public sealed class GoogleServiceAccountCredentialOnboardingService(ICredentialVault credentialVault)
{
    internal const string ServiceAccountMarker = "google-service-account:v1";
    internal const string MetadataSuffix = "/google-service-account-metadata";
    internal const string PrivateKeySuffix = "/google-service-account-private-key";
    private const string GoogleApisDnsSuffix = ".googleapis.com";

    private readonly ICredentialVault _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));

    public Task<GoogleServiceAccountCredentialIdentity> ImportAsync(
        string credentialReferenceId,
        ReadOnlyMemory<char> serviceAccountJson,
        CancellationToken cancellationToken = default) =>
        ImportCoreAsync(
            credentialReferenceId,
            serviceAccountJson,
            voiceCatalogEndpoint: null,
            synthesisEndpoint: null,
            cancellationToken);

    public Task<GoogleServiceAccountCredentialIdentity> ImportAsync(
        string credentialReferenceId,
        ReadOnlyMemory<char> serviceAccountJson,
        Uri voiceCatalogEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(voiceCatalogEndpoint);
        ValidateVoiceCatalogEndpoint(voiceCatalogEndpoint);
        return ImportCoreAsync(
            credentialReferenceId,
            serviceAccountJson,
            voiceCatalogEndpoint,
            synthesisEndpoint: null,
            cancellationToken);
    }

    public Task<GoogleServiceAccountCredentialIdentity> ImportAsync(
        string credentialReferenceId,
        ReadOnlyMemory<char> serviceAccountJson,
        Uri voiceCatalogEndpoint,
        Uri synthesisEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(voiceCatalogEndpoint);
        ArgumentNullException.ThrowIfNull(synthesisEndpoint);
        ValidateVoiceCatalogEndpoint(voiceCatalogEndpoint);
        ValidateSynthesisEndpoint(synthesisEndpoint);
        RequireSameOrigin(voiceCatalogEndpoint, synthesisEndpoint);
        return ImportCoreAsync(
            credentialReferenceId,
            serviceAccountJson,
            voiceCatalogEndpoint,
            synthesisEndpoint,
            cancellationToken);
    }

    public async Task<Uri> ResolveVoiceCatalogEndpointAsync(
        string credentialReferenceId,
        CancellationToken cancellationToken = default)
    {
        GoogleServiceAccountCredentialMetadata metadata = await ResolveMetadataAsync(
            credentialReferenceId,
            cancellationToken).ConfigureAwait(false);
        Uri endpoint = metadata.VoiceCatalogEndpoint
            ?? throw new InvalidOperationException(
                "Google service-account metadata has no persisted voice-catalog endpoint; reconfigure Google TTS through the 1.0.1 onboarding flow.");
        ValidateVoiceCatalogEndpoint(endpoint);
        return endpoint;
    }

    public async Task<Uri> ResolveSynthesisEndpointAsync(
        string credentialReferenceId,
        CancellationToken cancellationToken = default)
    {
        GoogleServiceAccountCredentialMetadata metadata = await ResolveMetadataAsync(
            credentialReferenceId,
            cancellationToken).ConfigureAwait(false);
        Uri endpoint = metadata.SynthesisEndpoint
            ?? throw new InvalidOperationException(
                "Google service-account metadata has no persisted synthesis endpoint; reconfigure Google TTS through the 1.0.1 onboarding flow.");
        ValidateSynthesisEndpoint(endpoint);
        if (metadata.VoiceCatalogEndpoint is not null)
        {
            ValidateVoiceCatalogEndpoint(metadata.VoiceCatalogEndpoint);
            RequireSameOrigin(metadata.VoiceCatalogEndpoint, endpoint);
        }
        return endpoint;
    }

    public async Task<bool> RemoveAsync(string credentialReferenceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialReferenceId))
            throw new ArgumentException("Credential reference is required.", nameof(credentialReferenceId));

        bool marker = await _credentialVault.DeleteAsync(new CredentialReference(credentialReferenceId), cancellationToken).ConfigureAwait(false);
        bool metadata = await _credentialVault.DeleteAsync(new CredentialReference(credentialReferenceId + MetadataSuffix), cancellationToken).ConfigureAwait(false);
        bool key = await _credentialVault.DeleteAsync(new CredentialReference(credentialReferenceId + PrivateKeySuffix), cancellationToken).ConfigureAwait(false);
        return marker || metadata || key;
    }

    private async Task<GoogleServiceAccountCredentialMetadata> ResolveMetadataAsync(
        string credentialReferenceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialReferenceId))
            throw new ArgumentException("Credential reference is required.", nameof(credentialReferenceId));

        using CredentialSecret? secret = await _credentialVault.ReadAsync(
            new CredentialReference(credentialReferenceId + MetadataSuffix),
            cancellationToken).ConfigureAwait(false);
        if (secret is null)
            throw new InvalidOperationException("Google service-account metadata is unavailable for the configured credential.");

        try
        {
            string metadataJson = new(secret.Value.Span);
            return JsonSerializer.Deserialize<GoogleServiceAccountCredentialMetadata>(metadataJson)
                ?? throw new InvalidDataException("Google service-account metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Google service-account metadata is malformed.", exception);
        }
    }

    private async Task<GoogleServiceAccountCredentialIdentity> ImportCoreAsync(
        string credentialReferenceId,
        ReadOnlyMemory<char> serviceAccountJson,
        Uri? voiceCatalogEndpoint,
        Uri? synthesisEndpoint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialReferenceId))
            throw new ArgumentException("Credential reference is required.", nameof(credentialReferenceId));
        if (serviceAccountJson.IsEmpty)
            throw new ArgumentException("Google service-account JSON is required.", nameof(serviceAccountJson));

        cancellationToken.ThrowIfCancellationRequested();
        GoogleServiceAccountCredentialMaterial material = Parse(serviceAccountJson.Span);

        CredentialReference markerReference = new(credentialReferenceId);
        CredentialReference metadataReference = new(credentialReferenceId + MetadataSuffix);
        CredentialReference privateKeyReference = new(credentialReferenceId + PrivateKeySuffix);

        string metadataJson = JsonSerializer.Serialize(new GoogleServiceAccountCredentialMetadata(
            material.ProjectId,
            material.ClientEmail,
            material.PrivateKeyId,
            material.TokenUri,
            voiceCatalogEndpoint,
            synthesisEndpoint));

        bool keyStored = false;
        bool metadataStored = false;
        try
        {
            await _credentialVault.StoreAsync(privateKeyReference, material.PrivateKey.AsMemory(), cancellationToken).ConfigureAwait(false);
            keyStored = true;
            await _credentialVault.StoreAsync(metadataReference, metadataJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            metadataStored = true;
            await _credentialVault.StoreAsync(markerReference, ServiceAccountMarker.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (metadataStored)
                await TryDeleteAsync(metadataReference).ConfigureAwait(false);
            if (keyStored)
                await TryDeleteAsync(privateKeyReference).ConfigureAwait(false);
            await TryDeleteAsync(markerReference).ConfigureAwait(false);
            throw;
        }

        return new GoogleServiceAccountCredentialIdentity(
            credentialReferenceId,
            material.ProjectId,
            material.ClientEmail,
            material.PrivateKeyId,
            material.TokenUri);
    }

    private async Task TryDeleteAsync(CredentialReference reference)
    {
        try
        {
            _ = await _credentialVault.DeleteAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original import exception. A subsequent explicit remove can retry cleanup.
        }
    }

    private static GoogleServiceAccountCredentialMaterial Parse(ReadOnlySpan<char> json)
    {
        byte[] utf8 = new byte[System.Text.Encoding.UTF8.GetByteCount(json)];
        try
        {
            _ = System.Text.Encoding.UTF8.GetBytes(json, utf8);
            using JsonDocument document = JsonDocument.Parse(utf8);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Google service-account credential must be a JSON object.");

            string type = RequiredString(root, "type");
            if (!string.Equals(type, "service_account", StringComparison.Ordinal))
                throw new InvalidDataException("The selected Google credential is not a service-account credential.");

            string projectId = RequiredString(root, "project_id");
            string clientEmail = RequiredString(root, "client_email");
            string privateKeyId = RequiredString(root, "private_key_id");
            string privateKey = RequiredString(root, "private_key");
            string tokenUriText = RequiredString(root, "token_uri");

            if (!clientEmail.EndsWith(".iam.gserviceaccount.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Google service-account email identity is invalid.");
            if (!privateKey.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal) ||
                !privateKey.Contains("END PRIVATE KEY", StringComparison.Ordinal))
                throw new InvalidDataException("Google service-account private key is not a PKCS#8 PEM key.");
            if (!Uri.TryCreate(tokenUriText, UriKind.Absolute, out Uri? tokenUri) || !IsTrustedGoogleOAuthTokenUri(tokenUri))
                throw new InvalidDataException("Google service-account token URI must use a trusted Google HTTPS API origin.");

            return new GoogleServiceAccountCredentialMaterial(projectId, clientEmail, privateKeyId, privateKey, tokenUri);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Google service-account credential JSON is malformed.", exception);
        }
        finally
        {
            Array.Clear(utf8);
        }
    }

    private static bool IsTrustedGoogleOAuthTokenUri(Uri tokenUri)
    {
        if (!string.Equals(tokenUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !tokenUri.IsDefaultPort ||
            !string.IsNullOrEmpty(tokenUri.UserInfo) ||
            !string.IsNullOrEmpty(tokenUri.Query) ||
            !string.IsNullOrEmpty(tokenUri.Fragment))
        {
            return false;
        }

        string host = tokenUri.DnsSafeHost;
        return host.EndsWith(GoogleApisDnsSuffix, StringComparison.OrdinalIgnoreCase) &&
               host.Length > GoogleApisDnsSuffix.Length;
    }

    private static void ValidateVoiceCatalogEndpoint(Uri endpoint)
    {
        ValidateGoogleApiEndpoint(endpoint, "voice-catalog");
    }

    private static void ValidateSynthesisEndpoint(Uri endpoint)
    {
        ValidateGoogleApiEndpoint(endpoint, "synthesis");
        if (!string.IsNullOrEmpty(endpoint.Query))
            throw new InvalidDataException("Google synthesis endpoint must not contain query data.");
        if (string.IsNullOrWhiteSpace(endpoint.AbsolutePath) || string.Equals(endpoint.AbsolutePath, "/", StringComparison.Ordinal))
            throw new InvalidDataException("Google synthesis endpoint must identify an explicit API path, not only an origin.");
    }

    private static void ValidateGoogleApiEndpoint(Uri endpoint, string label)
    {
        if (!endpoint.IsAbsoluteUri ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !endpoint.IsDefaultPort ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !IsGoogleApisHost(endpoint.DnsSafeHost))
        {
            throw new InvalidDataException(
                $"Google {label} endpoint must be a credential-free absolute HTTPS Google APIs URI on the default port without a fragment.");
        }
    }

    private static void RequireSameOrigin(Uri left, Uri right)
    {
        if (Uri.Compare(
                left,
                right,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidDataException(
                "Google voice-catalog and synthesis endpoints must use the same admitted Google API origin for this provider account.");
        }
    }

    private static bool IsGoogleApisHost(string host) =>
        string.Equals(host, "googleapis.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(GoogleApisDnsSuffix, StringComparison.OrdinalIgnoreCase);

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Google service-account credential is missing '{propertyName}'.");
        string? result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException($"Google service-account credential has an empty '{propertyName}'.");
        return result;
    }

    private sealed record GoogleServiceAccountCredentialMaterial(
        string ProjectId,
        string ClientEmail,
        string PrivateKeyId,
        string PrivateKey,
        Uri TokenUri);

    internal sealed record GoogleServiceAccountCredentialMetadata(
        string ProjectId,
        string ClientEmail,
        string PrivateKeyId,
        Uri TokenUri,
        Uri? VoiceCatalogEndpoint = null,
        Uri? SynthesisEndpoint = null);
}
