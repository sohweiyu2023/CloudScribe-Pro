using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Configuration;

public sealed class CloudScribeOptionsValidator : IValidateOptions<CloudScribeOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudScribeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        if (options.DiagnosticFileSizeMiB is < 1 or > 64)
        {
            failures.Add("DiagnosticFileSizeMiB must be between 1 and 64.");
        }

        if (options.DiagnosticDirectorySizeMiB < options.DiagnosticFileSizeMiB || options.DiagnosticDirectorySizeMiB > 512)
        {
            failures.Add("DiagnosticDirectorySizeMiB must be at least one file and no more than 512 MiB.");
        }

        if (options.DiagnosticMaximumFileCount is < 1 or > 4096)
        {
            failures.Add("DiagnosticMaximumFileCount must be between 1 and 4096.");
        }

        if (options.DiagnosticQueueCapacity is < 128 or > 16384)
        {
            failures.Add("DiagnosticQueueCapacity must be between 128 and 16384.");
        }

        if (options.SupportBundleMaximumMiB is < 1 or > 256)
        {
            failures.Add("SupportBundleMaximumMiB must be between 1 and 256.");
        }

        if (options.SupportBundleMaximumFileCount is < 1 or > 4096)
        {
            failures.Add("SupportBundleMaximumFileCount must be between 1 and 4096.");
        }

        if (options.StartupTimeoutSeconds is < 5 or > 120)
        {
            failures.Add("StartupTimeoutSeconds must be between 5 and 120.");
        }

        if (!string.IsNullOrWhiteSpace(options.AppDataDirectoryOverride)
            && !AppDataPathPolicy.TryResolveOverride(
                options.AppDataDirectoryOverride,
                out _,
                out string pathFailure))
        {
            failures.Add(pathFailure);
        }

        if (options.RemoteTelemetryEnabled)
        {
            failures.Add("RemoteTelemetryEnabled cannot be enabled before an explicit consent and endpoint implementation exists.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
