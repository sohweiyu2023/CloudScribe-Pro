namespace CloudScribe.Application.Generation;

public interface INativeMediaTool
{
    Task<NativeMediaToolResult> RunAsync(
        NativeMediaToolInvocation invocation,
        CancellationToken cancellationToken = default);
}
