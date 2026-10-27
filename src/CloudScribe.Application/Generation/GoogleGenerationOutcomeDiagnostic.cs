using System.Globalization;

namespace CloudScribe.Application.Generation;

/// <summary>Allowlisted metadata only; provider text and response bodies never enter diagnostics.</summary>
public static class GoogleGenerationOutcomeDiagnostic
{
    public static bool IsConfirmedTerminal(string? code)
    {
        if (code is "google-accepted" or "google-accepted-invalid-media")
            return true;
        return code is { Length: 15 } && code.StartsWith("google-http-", StringComparison.Ordinal)
            && int.TryParse(code.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int status)
            && status is >= 400 and < 500 and not 408 and not 429;
    }

    public static string Sanitize(string? code)
    {
        if (code is { Length: 15 } && code.StartsWith("google-http-", StringComparison.Ordinal)
            && int.TryParse(code.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int status)
            && status is >= 100 and <= 599)
            return code;

        return code switch
        {
            "google-accepted" or "google-accepted-invalid-media" or "google-transport-unknown"
                or "google-transport-timeout" or "google-dispatch-claimed"
                or "google-dispatch-outcome-unavailable" => code,
            _ => "google-outcome-unavailable",
        };
    }
}
