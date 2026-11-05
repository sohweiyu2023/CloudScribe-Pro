using System.Security.Cryptography;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

internal static class GoogleLongAudioValidation
{
    internal static void RequireAccessToken(string? token)
    {
        if (token is not { Length: > 0 and <= 16_384 }
            || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='))
            throw new InvalidOperationException("Long Audio bearer material is invalid or outside the protected-redaction bound.");
    }

    internal static string BindingDigest(Guid attemptId, string projectNumber, string location,
        string credentialReferenceId, string outputGcsUri, string payloadSha256) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { attemptId, projectNumber, location, credentialReferenceId, outputGcsUri, PayloadSha256 = payloadSha256 })));

    internal static void RequireIdentifier(string text, int maximum)
    {
        if (string.IsNullOrEmpty(text) || text.Length > maximum || !text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("Invalid Long Audio resource identifier.", nameof(text));
    }

    internal static void RequireScope(Guid id, string project, string location, string credential, string output)
    {
        if (id == Guid.Empty || string.IsNullOrEmpty(project) || project.Length > 20 || !project.All(char.IsAsciiDigit))
            throw new ArgumentException("An exact numeric Google project and nonempty attempt are required.", nameof(project));
        RequireIdentifier(location, 32);
        if (location.Any(char.IsAsciiLetterUpper) || string.IsNullOrWhiteSpace(credential) || credential.Length > 192
            || credential.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new ArgumentException("Invalid private credential binding.", nameof(credential));
        if (output is null || output.Length > 1024 || !output.StartsWith("gs://", StringComparison.Ordinal))
            throw new ArgumentException("An existing private bucket output is required.", nameof(output));
        int slash = output.IndexOf('/', 5);
        if (slash < 8) throw new ArgumentException("An exact bucket object is required.", nameof(output));
        string bucket = output[5..slash], name = output[(slash + 1)..];
        string[] segments = name.Split('/');
        string basename = segments[^1], attempt = id.ToString("N");
        if (bucket.Length > 63 || !bucket.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'z' or '-' or '.')
            || bucket.Contains("..", StringComparison.Ordinal) || !char.IsAsciiLetterOrDigit(bucket[0]) || !char.IsAsciiLetterOrDigit(bucket[^1])
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/')
            || segments.Any(p => p.Length == 0 || p is "." or "..")
            || !(string.Equals(basename, attempt + ".mp3", StringComparison.Ordinal)
                || string.Equals(basename, attempt + ".wav", StringComparison.Ordinal)))
            throw new ArgumentException("Output must be a unique attempt-bound private bucket object.", nameof(output));
    }

    internal static bool IsOperation(string? name, string project, string location)
    {
        string prefix = $"projects/{project}/locations/{location}/operations/";
        return name is { Length: <= 256 } && name.StartsWith(prefix, StringComparison.Ordinal)
            && name.Length > prefix.Length && name.AsSpan(prefix.Length).ToString().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    internal static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (names.Count >= 64 || !names.Add(member.Name)) throw new JsonException("Invalid operation member structure.");
                RequireUnique(member.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 64) throw new JsonException("Invalid operation array structure.");
            foreach (var item in value.EnumerateArray()) RequireUnique(item);
        }
    }
}
