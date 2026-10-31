using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Fixed categories and numeric status only. Never accepts response text, identifiers or secrets.</summary>
public static partial class GoogleCatalogRefreshDiagnostics
{
    private static readonly object HttpContextKey = new();
    private static readonly object PhaseContextKey = new();
    private static readonly object OAuthContextKey = new();
    private static readonly object MissingCredentialKey = new();

    private static object? Context(Exception exception, object key)
    {
        try { return exception.Data[key]; }
        catch (Exception failure) when (IsNonFatal(failure)) { return null; }
    }

    internal static T MissingCredential<T>(T exception) where T : Exception
    {
        exception.Data[MissingCredentialKey] = true;
        return exception;
    }

    // Preserve the original exception type while carrying only our typed local response metadata.
    internal static T WithHttpContext<T>(T exception, HttpStatusCode status, GoogleCatalogRefreshPhase phase) where T : Exception
    {
        exception.Data[HttpContextKey] = (int)status;
        exception.Data[PhaseContextKey] = phase;
        return exception;
    }

    internal static T WithOAuthFailure<T>(T exception, GoogleOAuthFailure reason) where T : Exception
    {
        exception.Data[OAuthContextKey] = reason;
        return exception;
    }

    public static GoogleOAuthFailure OAuthFailure(Exception exception) => Context(exception, OAuthContextKey) is GoogleOAuthFailure reason
        && Enum.IsDefined(reason) ? reason : GoogleOAuthFailure.None;

    public static int HttpStatus(Exception exception) => exception is HttpRequestException { StatusCode: { } status }
        ? (int)status : Context(exception, HttpContextKey) is int code and >= 100 and <= 599 ? code : 0;

    public static GoogleCatalogRefreshPhase? ReportedPhase(Exception exception, GoogleCatalogRefreshPhase? fallback) =>
        Context(exception, PhaseContextKey) is GoogleCatalogRefreshPhase phase && Enum.IsDefined(phase) ? phase : fallback;

    public static bool IsNonFatal(Exception exception) => exception is not OutOfMemoryException
        and not StackOverflowException and not AccessViolationException;

    public static string Category(Exception exception) => exception switch
    {
        CompletionGateException gate => "completion-" + gate.Reason.ToString().ToLowerInvariant(),
        _ when Context(exception, MissingCredentialKey) is true => "saved-credential-unavailable",
        OperationCanceledException => "cancelled-or-timeout",
        HttpRequestException { StatusCode: not null } => "http-rejected",
        HttpRequestException => "transport",
        SqliteException => "storage",
        JsonException => "invalid-json",
        CryptographicException => "credential-crypto",
        UnauthorizedAccessException => "local-access",
        InvalidDataException => "invalid-data",
        InvalidOperationException => "verification",
        TimeoutException => "timeout",
        _ => "unexpected",
    };

    public static void Phase(ILogger? logger, Guid refreshId, GoogleCatalogRefreshPhase phase)
    {
        try { if (logger is not null) PhaseMessage(logger, refreshId, phase); }
        catch (Exception exception) when (IsNonFatal(exception)) { /* Diagnostics must not affect refresh. */ }
    }

    public static void Http(ILogger? logger, Guid refreshId, GoogleCatalogRefreshPhase phase, HttpStatusCode status)
    {
        try { if (logger is not null) HttpMessage(logger, refreshId, phase, (int)status); }
        catch (Exception exception) when (IsNonFatal(exception)) { /* Diagnostics must not affect refresh. */ }
    }

    public static void Failure(ILogger? logger, Guid refreshId, GoogleCatalogRefreshPhase phase, Exception exception)
    {
        try
        {
            if (logger?.IsEnabled(LogLevel.Warning) != true) return;
            FailureMessage(logger, refreshId, phase, Category(exception), OAuthFailure(exception), HttpStatus(exception),
                exception is SqliteException sqlite ? sqlite.SqliteErrorCode : 0,
                exception is SqliteException extended ? extended.SqliteExtendedErrorCode : 0);
        }
        catch (Exception failure) when (IsNonFatal(failure)) { /* Preserve the original refresh failure. */ }
    }

    [LoggerMessage(6200, LogLevel.Information, "Google catalog refresh {RefreshId} phase {CatalogPhase}.")]
    private static partial void PhaseMessage(ILogger logger, Guid refreshId, GoogleCatalogRefreshPhase catalogPhase);

    [LoggerMessage(6201, LogLevel.Information, "Google catalog refresh {RefreshId} phase {CatalogPhase} HTTP {HttpStatus}.")]
    private static partial void HttpMessage(ILogger logger, Guid refreshId, GoogleCatalogRefreshPhase catalogPhase, int httpStatus);

    [LoggerMessage(6202, LogLevel.Warning, "Google catalog refresh {RefreshId} failed at {CatalogPhase}; category {CatalogErrorCategory}; OAuth {OAuthFailure}; HTTP {HttpStatus}; storage {StorageCode}/{StorageExtendedCode}.")]
    private static partial void FailureMessage(ILogger logger, Guid refreshId, GoogleCatalogRefreshPhase catalogPhase,
        string catalogErrorCategory, GoogleOAuthFailure oauthFailure, int httpStatus, int storageCode, int storageExtendedCode);
}
