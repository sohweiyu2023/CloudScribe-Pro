namespace CloudScribe.Domain.Security;

public sealed class SecretRedactionPolicy
{
    private static readonly string[] SensitiveHeaderNames =
    [
        "authorization",
        "proxy-authorization",
        "x-api-key",
        "api-key",
        "x-goog-api-key",
    ];

    private readonly string[] _literalSecrets;

    public SecretRedactionPolicy(IEnumerable<string> literalSecrets)
    {
        ArgumentNullException.ThrowIfNull(literalSecrets);
        _literalSecrets = literalSecrets
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static value => value.Length)
            .ToArray();
        if (_literalSecrets.Any(static value => value.Length < 8))
        {
            throw new ArgumentException("Registered secret literals must be at least eight characters to avoid unsafe broad redaction.", nameof(literalSecrets));
        }
    }

    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = text;
        foreach (var secret in _literalSecrets)
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        var lines = redacted.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = RedactSensitiveHeader(lines[index]);
        }
        return string.Join(Environment.NewLine, lines);
    }

    public IReadOnlyList<string> FindUnredactedRegisteredSecrets(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _literalSecrets.Where(secret => text.Contains(secret, StringComparison.Ordinal)).ToArray();
    }

    private static string RedactSensitiveHeader(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0)
        {
            return line;
        }

        var name = line[..colon].Trim();
        if (!SensitiveHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return line;
        }

        var leadingLength = line.Length - line.TrimStart().Length;
        var leading = line[..leadingLength];
        return $"{leading}{name}: [REDACTED]";
    }
}
