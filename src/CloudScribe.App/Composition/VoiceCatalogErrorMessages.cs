using System.Net;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public static class VoiceCatalogErrorMessages
{
    public static string For(Exception exception, GoogleCatalogRefreshPhase? phase = null)
    {
        var reported = GoogleCatalogRefreshDiagnostics.ReportedPhase(exception, phase);
        int status = GoogleCatalogRefreshDiagnostics.HttpStatus(exception);
        return Message(exception, status) + $" Diagnostic: {GoogleCatalogRefreshDiagnostics.Category(exception)}"
            + (reported is null ? string.Empty : $" at {reported}")
            + (GoogleCatalogRefreshDiagnostics.OAuthFailure(exception) is var oauth && oauth != GoogleOAuthFailure.None ? $"; OAuth {oauth}" : string.Empty)
            + (status == 0 ? "." : $"; HTTP {status}.");
    }

    private static string Message(Exception exception, int status) => GoogleCatalogRefreshDiagnostics.Category(exception) is "saved-credential-unavailable"
        ? "The saved Google connection's local credential material is unavailable in this Windows user context. Check the Windows account and Setups, and keep your original service-account file for recovery. No speech was requested and no key was changed."
        : status switch
        {
            (int)HttpStatusCode.Unauthorized => "Google did not accept the saved connection. Inspect connection status in Setups; do not paste or replace your key just to retry.",
            (int)HttpStatusCode.Forbidden => "Google refused voice access. Check Text-to-Speech API enablement, billing and service-account access in your Google project, then refresh. Your saved key was not changed.",
            _ => exception switch
            {
                OperationCanceledException => "Voice loading stopped or timed out. Check your internet connection, then choose Refresh real Google voices to retry.",
                HttpRequestException => "Could not reach Google to load voices. Check your internet connection and try Refresh real Google voices. No speech was requested.",
                _ => "The voice list could not be verified. Inspect the saved connection in Setups, check the language/search filters, then refresh. No speech was requested and no key was changed.",
            },
        };
}
