using CloudScribe.Application.Generation;

namespace CloudScribe.App.ViewModels;

public sealed record GoogleGenerationUiExecutionContext(
    GoogleGenerationUiQueueCoordinator Coordinator,
    GoogleGenerationUiExecutionSnapshot Snapshot,
    CloudScribe.Infrastructure.Generation.GoogleGenerationSubmissionEnvelope? SubmissionEnvelope = null);
