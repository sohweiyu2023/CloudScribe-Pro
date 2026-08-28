namespace CloudScribe.Infrastructure.Activation;

public interface ISingleInstanceCoordinator : IDisposable, IAsyncDisposable
{
    Task<bool> TryBecomePrimaryAsync(
        IReadOnlyList<string> activationArguments,
        CancellationToken cancellationToken = default);
}
