using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

/// <summary>
/// Binds the Stage6 shell only through a fresh asynchronous production runtime-request source.
/// The binder never creates authorization, pricing, queue, trust, or reconciliation evidence;
/// it validates the source result and delegates all authorization/context construction to the
/// production execution-context resolver immediately before generation.
/// </summary>
public sealed class Stage6GoogleGenerationShellBinder
{
    private readonly GoogleGenerationProductionExecutionContextResolver _executionContextResolver;

    public Stage6GoogleGenerationShellBinder(
        GoogleGenerationProductionExecutionContextResolver executionContextResolver)
    {
        _executionContextResolver = executionContextResolver
            ?? throw new ArgumentNullException(nameof(executionContextResolver));
    }

    public void Bind(
        ShellViewModel viewModel,
        Func<CancellationToken, Task<GoogleGenerationProductionRuntimeRequest>> resolveCurrentRuntimeRequest)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(resolveCurrentRuntimeRequest);

        viewModel.ConfigureStage6GoogleGeneration(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            GoogleGenerationProductionRuntimeRequest request = await resolveCurrentRuntimeRequest(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Stage6 production runtime request source returned no current request evidence.");

            request.Validate();
            cancellationToken.ThrowIfCancellationRequested();

            return await _executionContextResolver
                .ResolveAsync(request, cancellationToken)
                .ConfigureAwait(false);
        });
    }
}
