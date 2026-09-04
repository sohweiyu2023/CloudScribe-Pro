using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// User/request-originated Stage6 generation choices captured as one immutable transaction.
/// This type intentionally contains no authorization, pricing-current, trust, queue, or
/// reconciliation assertions; those must be resolved by production owners after capture.
/// </summary>
public sealed record GoogleGenerationProductionRequestIntent
{
    public required SpeechPlan Plan { get; init; }

    public required GoogleSpeechCompilationOptions CompilationOptions { get; init; }

    public required string AccountId { get; init; }

    public required string ProjectId { get; init; }

    public required string ModelId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    public GoogleGenerationProductionRequestIntent Validate()
    {
        ArgumentNullException.ThrowIfNull(Plan);
        ArgumentNullException.ThrowIfNull(CompilationOptions);
        RequireCanonical(AccountId, nameof(AccountId));
        RequireCanonical(ProjectId, nameof(ProjectId));
        RequireCanonical(ModelId, nameof(ModelId));
        RequireCanonical(IdempotencyKey, nameof(IdempotencyKey));
        CompilationOptions.Validate();
        return this;
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Google generation request intent identity is required.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains('\r')
            || value.Contains('\n')
            || value.Contains('\0'))
        {
            throw new InvalidOperationException(
                $"Google generation request intent identity '{parameterName}' is not canonical.");
        }
    }
}
