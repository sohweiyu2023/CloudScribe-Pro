using System.Text.Json;
using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Imports a Google service-account JSON credential into Windows-backed secret storage without
/// persisting the source JSON or an OAuth bearer token. The private key and non-secret identity
/// metadata are stored as separate credential entries so the private key remains below the
/// Windows Credential Manager blob limit.
/// </summary>
public sealed class GoogleServiceAccountCredentialOnboardingService(ICredentialVault credentialVault)
{
    internal const string ServiceAccountMarker = "google-service-account:v1";
    internal const string MetadataSuffix = "/google-service-account-metadata";
    internal const string PrivateKeySuffix = "/google-service-account-private-key";

    private readonly ICredentialVault _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));

    public async Task<GoogleServiceAccountCredentialIdentity> ImportAsync(
        string credentialReferenceId,
        ReadOnlyMemory<char> serviceAccountJson,
        CancellationToken cancellationToken = default)
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
            material.TokenUri));

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

    public async Task<bool> RemoveAsync(string credentialReferenceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialReferenceId))
            throw new ArgumentException("Credential reference is required.", nameof(credentialReferenceId));

        bool marker = await _credentialVault.DeleteAsync(new CredentialReference(credentialReferenceId), cancellationToken).ConfigureAwait(false);
        bool metadata = await _credentialVault.DeleteAsync(new CredentialReference(credentialReferenceId + MetadataSuffix), cancellationToken).ConfigureAwait(false);
        bool key = await _credentialVault.DeleteAsync(new CredentialReference(credentialReferenceId + PrivateKeySuffix), cancellationToken).ConfigureAwait(false);
        return marker || metadata || key;
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
            if (!Uri.TryCreate(tokenUriText, UriKind.Absolute, out Uri? tokenUri) ||
                !string.Equals(tokenUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Google service-account token URI must be an absolute HTTPS URI.");

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
        Uri TokenUri);
}

public sealed record GoogleServiceAccountCredentialIdentity(
    string CredentialReferenceId,
    string ProjectId,
    string ClientEmail,
    string PrivateKeyId,
    Uri TokenUri);
