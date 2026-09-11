using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

/// <summary>
/// Builds the Stage6 cache/trust namespace only from the exact Studio capture, the current
/// persisted Google account/capability evidence, the active pricing provenance, authenticated
/// governance bytes, and the runtime implementation assemblies. No caller-supplied trust token is
/// accepted and no authorization fact is inferred from UI state.
/// </summary>
internal sealed class GoogleTtsStudioTrustContextFactory(V222ControlSet controls)
{
    private readonly V222ControlSet _controls = controls ?? throw new ArgumentNullException(nameof(controls));

    public GenerationCacheTrustContext Create(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        GoogleGenerationProductionRequestIntent intent,
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capability,
        string pricingProvenanceId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);

        intent.Validate();
        account.Validate();
        capability.Validate(nowUtc);

        if (!string.Equals(selection.AccountStableId, account.AccountId, StringComparison.Ordinal)
            || !string.Equals(intent.AccountId, account.AccountId, StringComparison.Ordinal)
            || !string.Equals(capability.AccountId, account.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Google Studio trust evidence is not bound to one current persisted account.");
        }

        if (!string.Equals(selection.VoiceStableId, intent.CompilationOptions.VoiceName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Google Studio trust evidence voice does not match the exact compilation intent.");
        }

        string speechPlanIdentity = HashIdentity(
            "speech-plan",
            intent.Plan.ProvenanceId,
            intent.Plan.LanguageTag,
            selection.ExactText);
        string synthesisControlsIdentity = HashIdentity(
            "synthesis-controls",
            intent.CompilationOptions.LanguageCode,
            intent.CompilationOptions.VoiceName,
            intent.CompilationOptions.AudioEncoding,
            intent.CompilationOptions.MaximumPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string sampleFormatIdentity = HashIdentity(
            "sample-format",
            intent.CompilationOptions.AudioEncoding);
        string governancePolicyIdentity = HashBytes(
            "governance-policy",
            _controls.RuntimePolicySeedUtf8.Span);
        string providerFeatureIdentity = HashIdentity(
            "provider-features",
            capability.ProvenanceId,
            capability.MaximumCompiledPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", capability.AudioEncodings.OrderBy(static value => value, StringComparer.Ordinal)),
            string.Join(",", capability.VoiceNames.OrderBy(static value => value, StringComparer.Ordinal)));
        string accountCapabilityIdentity = HashIdentity(
            "account-capability",
            account.AccountId,
            account.Endpoint.GetLeftPart(UriPartial.Authority),
            account.Region,
            capability.ProvenanceId,
            capability.ObservedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            capability.ExpiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        return GoogleGenerationCacheTrustContextFactory.Create(
            account,
            capability,
            intent.CompilationOptions,
            intent.ProjectId,
            intent.ModelId,
            selection.VoiceFingerprint,
            speechPlanIdentity,
            synthesisControlsIdentity,
            sampleFormatIdentity,
            RuntimeVersion(typeof(GoogleGenerationProvider)),
            RuntimeVersion(typeof(GoogleSpeechPlanCompiler)),
            RuntimeVersion(typeof(SpeechPlan)),
            RuntimeVersion(typeof(SpeechText)),
            pricingProvenanceId,
            governancePolicyIdentity,
            providerFeatureIdentity,
            accountCapabilityIdentity,
            nowUtc);
    }

    private static string RuntimeVersion(Type implementationType)
    {
        ArgumentNullException.ThrowIfNull(implementationType);
        AssemblyName name = implementationType.Assembly.GetName();
        Version version = name.Version
            ?? throw new InvalidOperationException(
                $"Runtime assembly version is unavailable for {implementationType.FullName}.");
        return $"{implementationType.FullName}@{version}";
    }

    private static string HashIdentity(string scope, params string[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(static value => string.IsNullOrWhiteSpace(value)))
            throw new InvalidOperationException($"Google Studio {scope} identity contains missing current evidence.");

        string canonical = string.Join('\n', new[] { scope }.Concat(values));
        return $"{scope}:sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}";
    }

    private static string HashBytes(string scope, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (bytes.IsEmpty)
            throw new InvalidOperationException($"Google Studio {scope} identity cannot be derived from empty evidence.");
        return $"{scope}:sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }
}