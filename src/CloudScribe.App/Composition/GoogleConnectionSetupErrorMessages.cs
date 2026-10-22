using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace CloudScribe.App.Composition;

/// <summary>Actionable setup failures without displaying credentials or provider response details.</summary>
public static class GoogleConnectionSetupErrorMessages
{
    public static string For(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string action = exception switch
        {
            OperationCanceledException => "The connection check stopped or timed out. Check your internet connection, then inspect Setups before trying again.",
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Google did not accept the connection. Check that the service-account key is valid and belongs to the intended Google project.",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "Google refused access. Check Text-to-Speech API enablement, billing and service-account access in your Google project.",
            HttpRequestException => "Could not reach Google. Check your internet connection, then inspect Setups before trying again.",
            JsonException or FormatException or CryptographicException => "The service-account JSON could not be read. Use the complete downloaded JSON key file; keep it private and do not paste it into your document.",
            _ => "Check the required fields, complete service-account JSON and HTTPS Google URLs. Inspect saved connections in Setups before changing local connection names or retrying.",
        };
        return $"Google connection setup did not finish. {action} No speech was requested.";
    }
}
