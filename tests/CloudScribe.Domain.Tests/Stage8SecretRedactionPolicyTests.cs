using CloudScribe.Domain.Security;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8SecretRedactionPolicyTests
{
    private static readonly string[] UnsafeShortSecrets = ["short"];

    [Fact]
    public void RedactRemovesRegisteredSecretsAndSensitiveHeaders()
    {
        const string secret = "super-secret-token-123";
        var policy = new SecretRedactionPolicy(new[] { secret });
        var input = string.Join("\n",
            $"payload={secret}",
            "Authorization: Bearer should-never-log",
            "x-api-key: header-secret",
            "safe-header: keep-this");

        var output = policy.Redact(input);

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("should-never-log", output, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", output, StringComparison.Ordinal);
        Assert.Contains("safe-header: keep-this", output, StringComparison.Ordinal);
        Assert.Empty(policy.FindUnredactedRegisteredSecrets(output));
    }

    [Fact]
    public void ConstructorRejectsUnsafeShortLiteralSecret()
    {
        Assert.Throws<ArgumentException>(() => new SecretRedactionPolicy(UnsafeShortSecrets));
    }

    [Fact]
    public void FindUnredactedRegisteredSecretsFailsClosedForRawDiagnostic()
    {
        const string secretA = "credential-value-A";
        const string secretB = "credential-value-B";
        var policy = new SecretRedactionPolicy(new[] { secretA, secretB });

        var leaked = policy.FindUnredactedRegisteredSecrets($"safe text {secretB}");

        Assert.Equal(new[] { secretB }, leaked);
    }
}
