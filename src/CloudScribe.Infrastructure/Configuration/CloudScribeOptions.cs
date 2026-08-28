namespace CloudScribe.Infrastructure.Configuration;

public sealed class CloudScribeOptions
{
    public const string SectionName = "CloudScribe";

    public string? AppDataDirectoryOverride { get; set; }

    public int DiagnosticFileSizeMiB { get; set; } = 4;

    public int DiagnosticDirectorySizeMiB { get; set; } = 32;

    public int DiagnosticMaximumFileCount { get; set; } = 256;

    public int DiagnosticQueueCapacity { get; set; } = 2048;

    public int SupportBundleMaximumMiB { get; set; } = 48;

    public int SupportBundleMaximumFileCount { get; set; } = 256;

    public int StartupTimeoutSeconds { get; set; } = 20;

    public bool RemoteTelemetryEnabled { get; set; }
}
