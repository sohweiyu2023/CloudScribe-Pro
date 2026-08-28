namespace CloudScribe.Application.Startup;

public interface IApplicationInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
