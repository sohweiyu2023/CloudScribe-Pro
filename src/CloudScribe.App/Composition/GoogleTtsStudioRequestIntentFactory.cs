using System.Security.Cryptography;
using System.Text;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Converts the exact current Studio selection into Stage6 request intent while resolving the
/// synthesis model only from current persisted project/model authorization. This factory creates
/// request intent only; it never grants authorization, pricing, trust, spend, queue, or
/// reconciliation state.
/// </summary>
public sealed class GoogleTtsStudioRequestIntentFactory(
    IGoogleGenerationProjectAuthorizationStore projectAuthorizationStore,
    TimeProvider timeProvider)
{
    private readonly IGoogleGenerationProjectAuthorizationStore _projectAuthorizationStore =
        projectAuthorizationStore ?? throw new ArgumentNullException(nameof(projectAuthorizationStore));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<GoogleGenerationProductionRequestIntent> CreateAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        int maximumPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (maximumPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        GoogleGenerationProjectAuthorizationEvidence authorization =
            await _projectAuthorizationStore.LoadSingleCurrentForProjectAsync(
                selection.AccountStableId,
                selection.ProjectStableId,
                nowUtc,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No single current persisted Google synthesis model authorization is available for the selected account and project.");

        authorization.Validate(nowUtc);
        if (!authorization.IsCurrent(nowUtc)
            || !string.Equals(authorization.AccountId, selection.AccountStableId, StringComparison.Ordinal)
            || !string.Equals(authorization.ProjectId, selection.ProjectStableId, StringComparison.Ordinal)
            || !string.Equals(authorization.CapabilityProvenanceId, selection.CapabilityEvidenceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The current Google synthesis authorization is not bound to the selected authenticated account, project, and capability evidence.");
        }

        string languageCode = RequireCanonical(selection.LanguageCode, nameof(selection.LanguageCode));
        string voiceName = RequireCanonical(selection.VoiceStableId, nameof(selection.VoiceStableId));
        string provenanceId = BuildSpeechPlanProvenance(selection);
        var plan = new SpeechPlan(
            languageCode,
            [new SpeechText(selection.ExactText)],
            provenanceId);
        var compilationOptions = new GoogleSpeechCompilationOptions(
            languageCode,
            voiceName,
            "MP3",
            maximumPayloadBytes).Validate();

        RequestIdentity requestIdentity = BuildRequestIdentity(selection, authorization.ModelId, compilationOptions);
        return new GoogleGenerationProductionRequestIntent
        {
            Plan = plan,
            CompilationOptions = compilationOptions,
            AccountId = selection.AccountStableId,
            ProjectId = selection.ProjectStableId,
            ModelId = authorization.ModelId,
            IdempotencyKey = requestIdentity.IdempotencyKey,
            RequestRevision = requestIdentity.RequestRevision,
            CapturedAtUtc = selection.CapturedAtUtc,
        }.Validate();
    }

    private static RequestIdentity BuildRequestIdentity(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        string modelId,
        GoogleSpeechCompilationOptions options)
    {
        string revision = selection.RevisionId?.ToString("N") ?? "unsaved";
        string canonical = string.Join('\n',
            "cloudscribe-google-tts-v1",
            selection.DocumentId.ToString("N"),
            revision,
            selection.ProviderStableId,
            selection.AccountStableId,
            selection.ProjectStableId,
            modelId,
            selection.CapabilityEvidenceId,
            selection.VoiceStableId,
            selection.VoiceFingerprint,
            options.LanguageCode,
            options.AudioEncoding,
            options.MaximumPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            selection.ExactText);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        string digest = Convert.ToHexString(hash).ToLowerInvariant();

        // The full SHA-256 digest is the idempotency identity. RequestRevision is only a bounded
        // monotonic-domain discriminator required by the existing Stage6 contract; it is not used
        // as a security identity and therefore may safely be derived from the exact request digest.
        int requestRevision = (int)(BitConverter.ToUInt32(hash, 0) & 0x7fffffffU);
        return new RequestIdentity($"studio-google-tts:{digest}", requestRevision);
    }

    private static string BuildSpeechPlanProvenance(ShellViewModel.GoogleTtsStudioRequestSelection selection)
    {
        // Provenance identifies the exact local document revision and authenticated voice/capability
        // selection. It is request identity only and is never interpreted as authorization evidence.
        string revision = selection.RevisionId?.ToString("N") ?? "unsaved";
        return $"studio:{selection.DocumentId:N}:{revision}:{selection.VoiceFingerprint}:{selection.CapabilityEvidenceId}";
    }

    private static string RequireCanonical(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Google TTS request identity is required.", parameterName);
        string normalized = value.Trim();
        if (!string.Equals(value, normalized, StringComparison.Ordinal)
            || normalized.Contains('\r')
            || normalized.Contains('\n')
            || normalized.Contains('\0'))
        {
            throw new InvalidOperationException($"Google TTS request identity '{parameterName}' is not canonical.");
        }
        return normalized;
    }

    private sealed record RequestIdentity(string IdempotencyKey, int RequestRevision);
}
