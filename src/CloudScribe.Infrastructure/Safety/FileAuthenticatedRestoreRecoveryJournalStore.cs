using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed class FileAuthenticatedRestoreRecoveryJournalStore : IDisposable
{
    private const string FormatVersion = "cloudscribe-restore-recovery-journal-v1";
    private readonly string _journalPath;
    private readonly byte[] _authenticationKey;
    private bool _disposed;

    public FileAuthenticatedRestoreRecoveryJournalStore(
        string journalPath,
        ReadOnlySpan<byte> authenticationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (authenticationKey.Length < 32)
            throw new ArgumentException("Restore recovery journal authentication requires at least 256 bits of key material.", nameof(authenticationKey));

        _journalPath = Path.GetFullPath(journalPath);
        if (!Path.IsPathFullyQualified(_journalPath))
            throw new InvalidOperationException("Restore recovery journal path must resolve to an absolute path.");

        _authenticationKey = authenticationKey.ToArray();
    }

    public async Task SaveAsync(
        RestoreTransactionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(journal);
        cancellationToken.ThrowIfCancellationRequested();

        var parent = Path.GetDirectoryName(_journalPath)
            ?? throw new InvalidOperationException("Restore recovery journal path has no parent directory.");
        Directory.CreateDirectory(parent);
        RequirePhysicalDirectoryChain(parent);

        var payload = JsonSerializer.SerializeToUtf8Bytes(journal);
        var mac = HMACSHA256.HashData(_authenticationKey, payload);
        var document = string.Join(
            '\n',
            FormatVersion,
            Convert.ToBase64String(payload),
            Convert.ToHexString(mac).ToLowerInvariant(),
            string.Empty);

        var temporaryPath = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, document, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _journalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    public async Task<RestoreTransactionJournal?> LoadAuthenticatedAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_journalPath))
            return null;

        var parent = Path.GetDirectoryName(_journalPath)
            ?? throw new InvalidOperationException("Restore recovery journal path has no parent directory.");
        RequirePhysicalDirectoryChain(parent);

        var document = await File.ReadAllTextAsync(_journalPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var lines = document.Split('\n', StringSplitOptions.None);
        if (lines.Length < 3 || !string.Equals(lines[0].TrimEnd('\r'), FormatVersion, StringComparison.Ordinal))
            throw new InvalidDataException("Restore recovery journal format is not recognized.");

        byte[] payload;
        byte[] expectedMac;
        try
        {
            payload = Convert.FromBase64String(lines[1].Trim());
            expectedMac = Convert.FromHexString(lines[2].Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Restore recovery journal authentication envelope is malformed.", ex);
        }

        try
        {
            var observedMac = HMACSHA256.HashData(_authenticationKey, payload);
            try
            {
                if (expectedMac.Length != observedMac.Length ||
                    !CryptographicOperations.FixedTimeEquals(expectedMac, observedMac))
                    throw new InvalidDataException("Restore recovery journal authentication failed.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(observedMac);
            }

            return JsonSerializer.Deserialize<RestoreTransactionJournal>(payload)
                ?? throw new InvalidDataException("Restore recovery journal payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Restore recovery journal payload is invalid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(expectedMac);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_authenticationKey);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void RequirePhysicalDirectoryChain(string directory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Restore recovery journal path may not traverse a reparse-point directory: {current.FullName}");
            current = current.Parent;
        }
    }
}
