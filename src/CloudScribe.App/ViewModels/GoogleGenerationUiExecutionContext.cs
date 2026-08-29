using CloudScribe.Application.Generation;

namespace CloudScribe.App.ViewModels;

public sealed record GoogleGenerationUiExecutionContext(
    GoogleGenerationUiQueueCoordinator Coordinator,
    GoogleGenerationUiExecutionSnapshot Snapshot);
