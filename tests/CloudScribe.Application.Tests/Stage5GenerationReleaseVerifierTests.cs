using System.Security.Cryptography;
using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationReleaseVerifierTests
{
    [Fact]
    public void VerifyAcceptsExactBytesAndRejectsTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "release.wav");
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(path, bytes);
            var outputSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var mediaSha = new string('a', 64);
            var receipt = GenerationReleaseReceipt.Create(
                Guid.NewGuid(),
                1,
                "pricing-prov",
                "approval-1",
                path,
                outputSha,
                new[]
                {
                    new GenerationReleaseSegmentReceipt(Guid.NewGuid(), "cache-key", mediaSha, "proof-prov", true),
                });

            var verifier = new GenerationReleaseVerifier(1024);
            Assert.True(verifier.Verify(receipt).IsValid);

            File.WriteAllBytes(path, new byte[] { 9, 9, 9 });
            var tampered = verifier.Verify(receipt);
            Assert.False(tampered.IsValid);
            Assert.Equal("output-hash-mismatch", tampered.DiagnosticCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
