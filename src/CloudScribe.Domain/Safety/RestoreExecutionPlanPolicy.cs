namespace CloudScribe.Domain.Safety;

public static class RestoreExecutionPlanPolicy
{
    public static RestoreExecutionPlan PrepareVerified(
        string stagingRoot,
        string restoreRoot,
        BackupRestoreManifest manifest,
        IReadOnlyList<RestoreManifestFileBinding> bindings,
        long maximumTotalBytes,
        int maximumFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0)
            throw new InvalidOperationException("Restore execution requires at least one verified staged file binding.");

        var canonicalStagingRoot = Path.GetFullPath(stagingRoot);
        RestoreManifestContentBinding.Verify(canonicalStagingRoot, bindings);

        var plan = RestoreExecutionPlan.Create(restoreRoot, manifest, maximumTotalBytes, maximumFiles);
        if (plan.Steps.Count != bindings.Count)
            throw new InvalidOperationException("Restore plan file count differs from the verified staged-content binding set.");

        var bindingByPath = bindings.ToDictionary(
            static x => x.RelativePath,
            StringComparer.OrdinalIgnoreCase);

        foreach (var step in plan.Steps)
        {
            if (!bindingByPath.TryGetValue(step.RelativePath, out var binding))
                throw new InvalidOperationException("Restore plan contains a file that was not verified in staged content.");
            if (binding.LengthBytes != step.Length || !string.Equals(binding.Sha256Hex, step.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Restore plan metadata differs from the verified staged-content binding.");
        }

        return plan;
    }
}
