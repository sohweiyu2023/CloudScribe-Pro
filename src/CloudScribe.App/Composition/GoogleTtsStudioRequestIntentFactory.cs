using System.Security.Cryptography;
using System.Text;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Converts the exact current Studio selection into Stage6 request intent while resolving the
/// selected provider-returned voice only from current persisted project/model authorization. The
/// Voice Lab snapshot ID and the capability provenance are deliberately kept as separate identity
/// domains: the former proves which authenticated catalog selection the user saw, while the latter
/// binds Stage6 to the current persisted provider capability evidence.
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
        GoogleCapabilitySnapshot currentCapability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(currentCapability);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        ValidateCapabilityBinding(selection, currentCapability, nowUtc);
        string languageCode = RequireCanonical(selection.LanguageCode, nameof(selection.LanguageCode));
        string voiceName = RequireCanonical(selection.VoiceStableId, nameof(selection.VoiceStableId));
        currentCapability.RequireVoiceAndEncodingSupported(voiceName, "MP3", nowUtc);

        GoogleGenerationProjectAuthorizationEvidence authorization = await LoadAuthorizationAsync(
            selection,
            currentCapability,
            voiceName,
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        var plan = new SpeechPlan(
            languageCode,
            [new SpeechText(selection.ExactText)],
            BuildSpeechPlanProvenance(selection, currentCapability.ProvenanceId));
        var compilationOptions = new GoogleSpeechCompilationOptions(
            languageCode,
            voiceName,
            "MP3",
            currentCapability.MaximumCompiledPayloadBytes).Validate();
        RequestIdentity requestIdentity = BuildRequestIdentity(
            selection,
            authorization.ModelId,
            currentCapability.ProvenanceId,
            compilationOptions);

        return CreateIntent(selection, authorization, plan, compilationOptions, requestIdentity);
    }

    private async Task<GoogleGenerationProjectAuthorizationEvidence> LoadAuthorizationAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        GoogleCapabilitySnapshot currentCapability,
        string voiceName,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        GoogleGenerationProjectAuthorizationEvidence authorization =
            await _projectAuthorizationStore.LoadCurrentAsync(
                selection.AccountStableId,
                selection.ProjectStableId,
                voiceName,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No current persisted Google synthesis authorization is available for the selected real voice.");

        authorization.Validate(nowUtc);
        if (!authorization.IsCurrent(nowUtc)
            || !string.Equals(authorization.AccountId, selection.AccountStableId, StringComparison.Ordinal)
            || !string.Equals(authorization.ProjectId, selection.ProjectStableId, StringComparison.Ordinal)
            || !string.Equals(authorization.ModelId, voiceName, StringComparison.Ordinal)
            || !string.Equals(authorization.CapabilityProvenanceId, currentCapability.ProvenanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The current Google synthesis authorization is not bound to the selected account, project, real voice, and current capability provenance.");
        }

        return authorization;
    }

    private static void ValidateCapabilityBinding(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        GoogleCapabilitySnapshot currentCapability,
        DateTimeOffset nowUtc)
    {
        currentCapability.Validate(nowUtc);
        if (currentCapability.IsStale(nowUtc))
            throw new InvalidOperationException("Current Google capability evidence is stale. Refresh the authenticated voice catalog before preparing generation.");
        if (!string.Equals(currentCapability.AccountId, selection.AccountStableId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The current Google capability evidence is not bound to the selected authenticated account.");
        }
    }

    private static GoogleGenerationProductionRequestIntent CreateIntent(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        GoogleGenerationProjectAuthorizationEvidence authorization,
        SpeechPlan plan,
        GoogleSpeechCompilationOptions compilationOptions,
        RequestIdentity requestIdentity) =>
        new GoogleGenerationProductionRequestIntent
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

    private static RequestIdentity BuildRequestIdentity(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        string modelId,
        string capabilityProvenanceId,
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
            capabilityProvenanceId,
            selection.VoiceStableId,
            selection.VoiceFingerprint,
            options.LanguageCode,
            options.AudioEncoding,
            options.MaximumPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            selection.ExactText);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        string digest = Convert.ToHexString(hash).ToLowerInvariant();
        int requestRevision = (int)(BitConverter.ToUInt32(hash, 0) & 0x7fffffffU);
        return new RequestIdentity($"studio-google-tts:{digest}", requestRevision);
    }

    private static string BuildSpeechPlanProvenance(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        string capabilityProvenanceId)
    {
        string revision = selection.RevisionId?.ToString("N") ?? "unsaved";
        return $"studio:{selection.DocumentId:N}:{revision}:{selection.VoiceFingerprint}:{selection.CapabilityEvidenceId}:{capabilityProvenanceId}";
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
