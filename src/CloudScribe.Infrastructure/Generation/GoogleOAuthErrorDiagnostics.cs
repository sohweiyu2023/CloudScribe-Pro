using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

internal static class GoogleOAuthErrorDiagnostics
{
    internal const int MaximumBytes = 8192;

    internal static GoogleOAuthFailure Parse(byte[] body)
    {
        if (body.Length > MaximumBytes) return GoogleOAuthFailure.TooLarge;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.EnumerateObject().Count(x => string.Equals(x.Name, "error", StringComparison.Ordinal)) > 1) return GoogleOAuthFailure.Malformed;
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.String) return GoogleOAuthFailure.Unrecognized;
            return error.GetString() switch
            {
                "invalid_grant" => GoogleOAuthFailure.InvalidGrant,
                "invalid_client" => GoogleOAuthFailure.InvalidClient,
                "unauthorized_client" => GoogleOAuthFailure.UnauthorizedClient,
                "invalid_scope" => GoogleOAuthFailure.InvalidScope,
                "invalid_request" => GoogleOAuthFailure.InvalidRequest,
                "access_denied" => GoogleOAuthFailure.AccessDenied,
                "temporarily_unavailable" => GoogleOAuthFailure.TemporarilyUnavailable,
                "server_error" => GoogleOAuthFailure.ServerError,
                _ => GoogleOAuthFailure.Unrecognized,
            };
        }
        catch (JsonException) { return GoogleOAuthFailure.Malformed; }
    }

    internal static async Task<GoogleOAuthFailure> ReadAsync(HttpContent content, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (content.Headers.ContentLength is > MaximumBytes) return GoogleOAuthFailure.TooLarge;
            using var stream = await content.ReadAsStreamAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            byte[] buffer = new byte[MaximumBytes + 1];
            int used = 0, read;
            while (used < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(used), deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false)) > 0) used += read;
            return used > MaximumBytes ? GoogleOAuthFailure.TooLarge : Parse(buffer.AsSpan(0, used).ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return GoogleOAuthFailure.TimedOut; }
        catch (Exception exception) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(exception)) { return GoogleOAuthFailure.Unreadable; }
    }
}
