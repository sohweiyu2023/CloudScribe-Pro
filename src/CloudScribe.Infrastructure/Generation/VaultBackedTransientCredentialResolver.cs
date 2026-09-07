using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Resolves explicitly configured provider credentials from the Windows-backed local vault.
/// v1.0.0 bearer-token references remain readable for upgrade compatibility. v1.0.1 Google
/// service-account references are converted to short-lived OAuth access tokens on demand and
/// the resulting bearer token is never persisted.
/// </summary>
public sealed class VaultBackedTransientCredentialResolver : ITransientCredentialResolver
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private const string JwtBearerGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    private static readonly TimeSpan AssertionLifetime = TimeSpan.FromMinutes(55);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly ICredentialVault _credentialVault;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, CachedAccessToken> _cache = new(StringComparer.Ordinal);

    public VaultBackedTransientCredentialResolver(
        ICredentialVault credentialVault,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<string> ResolveAccessTokenAsync(
        string credentialReferenceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialReferenceId))
            throw new ArgumentException("Credential reference is required.", nameof(credentialReferenceId));

        CredentialReference reference = new(credentialReferenceId);
        using CredentialSecret? secret = await _credentialVault.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        if (secret is null)
            throw new InvalidOperationException("The explicitly configured provider credential reference is unavailable.");

        string storedMaterial = new(secret.Value.Span);
        if (string.IsNullOrWhiteSpace(storedMaterial))
            throw new InvalidOperationException("The explicitly configured provider credential contains no authentication material.");

        if (!string.Equals(storedMaterial, GoogleServiceAccountCredentialOnboardingService.ServiceAccountMarker, StringComparison.Ordinal))
            return storedMaterial;

        return await ResolveServiceAccountAccessTokenAsync(credentialReferenceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveServiceAccountAccessTokenAsync(
        string credentialReferenceId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        if (TryGetCached(credentialReferenceId, nowUtc, out string? cached))
            return cached;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowUtc = _timeProvider.GetUtcNow();
            if (TryGetCached(credentialReferenceId, nowUtc, out cached))
                return cached;

            GoogleServiceAccountCredentialOnboardingService.GoogleServiceAccountCredentialMetadata metadata =
                await ReadMetadataAsync(credentialReferenceId, cancellationToken).ConfigureAwait(false);
            using CredentialSecret privateKey = await ReadRequiredSecretAsync(
                credentialReferenceId + GoogleServiceAccountCredentialOnboardingService.PrivateKeySuffix,
                cancellationToken).ConfigureAwait(false);

            string assertion = CreateSignedAssertion(metadata, privateKey.Value.Span, nowUtc);
            using FormUrlEncodedContent form = new(new Dictionary<string, string>
            {
                ["grant_type"] = JwtBearerGrantType,
                ["assertion"] = assertion,
            });
            using HttpRequestMessage request = new(HttpMethod.Post, metadata.TokenUri) { Content = form };
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Google OAuth token exchange failed with HTTP {(int)response.StatusCode}.");

            OAuthTokenResponse tokenResponse = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Google OAuth token exchange returned an empty response.");
            if (string.IsNullOrWhiteSpace(tokenResponse.access_token))
                throw new InvalidDataException("Google OAuth token exchange returned no access token.");
            if (!string.Equals(tokenResponse.token_type, "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Google OAuth token exchange returned an unexpected token type.");
            if (tokenResponse.expires_in <= 0 || tokenResponse.expires_in > 3600)
                throw new InvalidDataException("Google OAuth token exchange returned an invalid token lifetime.");

            DateTimeOffset expiresUtc = nowUtc.AddSeconds(tokenResponse.expires_in);
            _cache[credentialReferenceId] = new CachedAccessToken(tokenResponse.access_token, expiresUtc);
            return tokenResponse.access_token;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<GoogleServiceAccountCredentialOnboardingService.GoogleServiceAccountCredentialMetadata> ReadMetadataAsync(
        string credentialReferenceId,
        CancellationToken cancellationToken)
    {
        using CredentialSecret secret = await ReadRequiredSecretAsync(
            credentialReferenceId + GoogleServiceAccountCredentialOnboardingService.MetadataSuffix,
            cancellationToken).ConfigureAwait(false);
        try
        {
            string metadataJson = new(secret.Value.Span);
            return JsonSerializer.Deserialize<GoogleServiceAccountCredentialOnboardingService.GoogleServiceAccountCredentialMetadata>(metadataJson)
                ?? throw new InvalidDataException("Google service-account metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Google service-account metadata is malformed.", exception);
        }
    }

    private async Task<CredentialSecret> ReadRequiredSecretAsync(string targetName, CancellationToken cancellationToken)
    {
        CredentialSecret? secret = await _credentialVault.ReadAsync(new CredentialReference(targetName), cancellationToken).ConfigureAwait(false);
        return secret ?? throw new InvalidOperationException("Required Google service-account credential material is unavailable.");
    }

    private static string CreateSignedAssertion(
        GoogleServiceAccountCredentialOnboardingService.GoogleServiceAccountCredentialMetadata metadata,
        ReadOnlySpan<char> privateKeyPem,
        DateTimeOffset nowUtc)
    {
        long issuedAt = nowUtc.ToUnixTimeSeconds();
        long expiresAt = nowUtc.Add(AssertionLifetime).ToUnixTimeSeconds();
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["kid"] = metadata.PrivateKeyId,
        });
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = metadata.ClientEmail,
            ["scope"] = CloudPlatformScope,
            ["aud"] = metadata.TokenUri.AbsoluteUri,
            ["iat"] = issuedAt,
            ["exp"] = expiresAt,
        });

        try
        {
            string signingInput = Base64Url(header) + "." + Base64Url(payload);
            byte[] signingBytes = Encoding.ASCII.GetBytes(signingInput);
            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(privateKeyPem);
                byte[] signature = rsa.SignData(signingBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                try
                {
                    return signingInput + "." + Base64Url(signature);
                }
                finally
                {
                    Array.Clear(signature);
                }
            }
            finally
            {
                Array.Clear(signingBytes);
            }
        }
        finally
        {
            Array.Clear(header);
            Array.Clear(payload);
        }
    }

    private bool TryGetCached(string credentialReferenceId, DateTimeOffset nowUtc, out string? token)
    {
        if (_cache.TryGetValue(credentialReferenceId, out CachedAccessToken? cached) &&
            cached.ExpiresUtc - RefreshSkew > nowUtc)
        {
            token = cached.Token;
            return true;
        }

        _cache.Remove(credentialReferenceId);
        token = null;
        return false;
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record CachedAccessToken(string Token, DateTimeOffset ExpiresUtc);

    private sealed record OAuthTokenResponse(string access_token, string token_type, int expires_in);
}
