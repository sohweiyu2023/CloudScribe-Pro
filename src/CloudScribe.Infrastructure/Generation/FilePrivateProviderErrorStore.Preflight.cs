namespace CloudScribe.Infrastructure.Generation;

internal sealed partial class FilePrivateProviderErrorStore
{
    public bool PrepareProtectedCapture()
    {
        lock (_gate)
        {
            string? probe = null;
            try
            {
                ValidateDirectory();
                // Save must be able to read the existing store, not merely create a new probe.
                var existing = ReadLegacyRecords();
                foreach (bool success in new[] { true })
                {
                    var pool = existing.Where(x => !IsArticleError(x) && IsSuccessMetadata(x) == success).ToArray();
                    if (pool.Length < 8) continue;
                    var oldest = pool.OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).First();
                    if (new FileInfo(ExistingRecordPath(oldest)).IsReadOnly) return false;
                }
                byte[] marker = "Synthetic private capture readiness; no customer content"u8.ToArray();
                byte[] protectedBytes = PrivateRecordProtection.Protect(marker);
                probe = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
                using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(protectedBytes); file.Flush(flushToDisk: true); }
                using (var read = new FileStream(probe, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (read.Length is <= 0 or > PrivateRecordProtection.MaximumProtectedBytes) return false;
                    var persisted = new byte[(int)read.Length]; read.ReadExactly(persisted);
                    if (!PrivateRecordProtection.Unprotect(persisted).AsSpan().SequenceEqual(marker)) return false;
                }
                return true;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { return false; }
            finally
            {
                if (probe is not null && File.Exists(probe))
                { ValidateDirectory(); RejectLink(new FileInfo(probe)); File.Delete(probe); }
            }
        }
    }
}
