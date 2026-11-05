using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

/// <summary>Production resolution remains the default. Disposable native UI tests can inject a confined fake provider adapter.</summary>
public interface IGoogleGenerationExecutionContextResolver
{
    Task<GoogleGenerationUiExecutionContext> ResolveAsync(GoogleGenerationProductionRuntimeRequest request, CancellationToken cancellationToken = default);
}
