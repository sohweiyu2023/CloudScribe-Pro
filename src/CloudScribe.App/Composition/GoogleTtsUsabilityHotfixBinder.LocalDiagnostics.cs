using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private string ReportLocalFailure(LocalRequestStage stage, Exception error, ChirpRequestReview? review = null)
    {
        var failure = LocalRequestDiagnostics.Failure(catalogLogger, Guid.NewGuid(), stage, error,
            DateTimeOffset.UtcNow, review?.ExpiresAtUtc);
        return LocalRequestDiagnostics.UserMessage(failure);
    }
}
