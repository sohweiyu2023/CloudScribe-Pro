namespace CloudScribe.Application.Diagnostics;

public interface ISupportBundleService
{
    Task<SupportBundlePreview> PreviewAsync(CancellationToken cancellationToken = default);

    Task<string> CreateAsync(string destinationDirectory, CancellationToken cancellationToken = default);
}
