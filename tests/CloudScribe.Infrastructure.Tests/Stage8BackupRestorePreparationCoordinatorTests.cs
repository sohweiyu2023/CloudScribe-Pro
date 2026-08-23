using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage8BackupRestorePreparationCoordinatorTests
{
    [Fact]
    public void Valid_signature_and_safe_archive_reach_staging()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "backup.zip");
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("project/data.txt", CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write("safe project data");
            }

            var manifest = Encoding.UTF8.GetBytes("{\"schema\":\"cloudscribe-backup-v1\"}");
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var signature = signer.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var publicKey = signer.ExportSubjectPublicKeyInfoPem();

            var result = BackupRestorePreparationCoordinator.Prepare(
                archivePath,
                Path.Combine(root, "staging"),
                manifest,
                signature,
                publicKey,
                schemaSupported: true);

            Assert.True(result.Decision.MayRestore);
            Assert.Equal(1, result.Staging.FilesExtracted);
            Assert.True(File.Exists(Path.Combine(result.Staging.StagingDirectory, "project", "data.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Invalid_manifest_signature_never_creates_staging_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "backup.zip");
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("project/data.txt", CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("safe project data");
            }

            var manifest = Encoding.UTF8.GetBytes("{\"schema\":\"cloudscribe-backup-v1\"}");
            using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var wrongSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var signature = wrongSigner.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var stagingRoot = Path.Combine(root, "staging");

            Assert.Throws<InvalidDataException>(() => BackupRestorePreparationCoordinator.Prepare(
                archivePath,
                stagingRoot,
                manifest,
                signature,
                trusted.ExportSubjectPublicKeyInfoPem(),
                schemaSupported: true));
            Assert.False(Directory.Exists(stagingRoot));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
