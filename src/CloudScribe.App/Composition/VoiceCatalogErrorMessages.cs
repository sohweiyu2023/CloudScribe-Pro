using System.Net;

namespace CloudScribe.App.Composition;

public static class VoiceCatalogErrorMessages
{
    public static string For(Exception exception) => exception switch
    {
        OperationCanceledException => "Voice loading stopped or timed out. Check your internet connection, then choose Refresh real Google voices to retry.",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Google did not accept the saved connection. Inspect connection status in Setups; do not paste or replace your key just to retry.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "Google refused voice access. Check Text-to-Speech API enablement, billing and service-account access in your Google project, then refresh. Your saved key was not changed.",
        HttpRequestException => "Could not reach Google to load voices. Check your internet connection and try Refresh real Google voices. No speech was requested.",
        _ => "The voice list could not be verified. Inspect the saved connection in Setups, check the language/search filters, then refresh. No speech was requested and no key was changed.",
    };
}
