namespace CloudScribe.Domain.Safety;

public sealed record RestoreExecutionPlan(
    string StagingRoot,
    IReadOnlyList<RestoreManifestFileBinding> Bindings)
{
    public RestoreExecutionPlan Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StagingRoot);
        ArgumentNullException.ThrowIfNull(Bindings);
        if (Bindings.Count == 0)
            throw new InvalidOperationException("Restore execution plan must contain at least one verified file binding.");
        foreach (var binding in Bindings)
            binding.Validate();
        return this;
    }
}

public static class RestoreExecutionPlanPolicy
{
    public static RestoreExecutionPlan PrepareVerified(
        string stagingRoot,
        IReadOnlyList<RestoreManifestFileBinding> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(bindings);
        var plan = new RestoreExecutionPlan(Path.GetFullPath(stagingRoot), bindings).Validate();
        RestoreManifestContentBinding.Verify(plan.StagingRoot, plan.Bindings);
        return plan;
    }
}
