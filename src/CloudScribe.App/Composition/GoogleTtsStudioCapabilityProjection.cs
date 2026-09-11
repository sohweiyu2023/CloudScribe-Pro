using System.Text;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Projects the narrow capability needed for a short synchronous Studio request from the exact
/// current persisted synthesis evidence plus the selected provider-returned voice. Google publishes
/// a 5,000-byte synchronous input-content limit. CloudScribe also uses that same number as a more
/// conservative local compiled-envelope ceiling; that local ceiling is intentionally stricter and
/// is not represented as Google's HTTP request-size limit.
/// </summary>
internal static class GoogleTtsStudioCapabilityProjection
{
    // Provider contract reference (verified 2026-09-11):
    // https://docs.cloud.google.com/text-to-speech/quotas
    internal const int SynchronousInputContentLimitBytes = 5_000;
    internal const int ConservativeCompiledEnvelopeLimitBytes = 5_000;

    public static GoogleCapabilitySnapshot Create(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        GoogleTtsStudioPreflightEvidenceResolver.PreflightEvidence preflight)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(preflight);
        preflight.Validate();

        GoogleGenerationProductionEvidence production = preflight.Production;
        production.Validate(preflight.CapturedAtUtc);

        if (!string.Equals(selection.AccountStableId, production.Account.Reference.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Google Studio capability projection is not bound to the current persisted account.");
        }

        if (string.IsNullOrWhiteSpace(selection.VoiceStableId))
            throw new InvalidOperationException("Google Studio capability projection has no selected real voice.");

        int inputBytes = Encoding.UTF8.GetByteCount(selection.ExactText);
        if (inputBytes <= 0)
            throw new InvalidOperationException("Google Studio synthesis input is empty.");
        if (inputBytes > SynchronousInputContentLimitBytes)
        {
            throw new InvalidOperationException(
                $"Google synchronous Text-to-Speech accepts at most {SynchronousInputContentLimitBytes:N0} UTF-8 input bytes per request; the current document contains {inputBytes:N0} bytes. Shorten the document before generation.");
        }

        return new GoogleCapabilitySnapshot(
            production.Account.Reference.AccountId,
            production.Capability.Snapshot.ProvenanceId,
            production.Capability.Snapshot.CapturedAtUtc,
            production.Capability.ExpiresAtUtc,
            new HashSet<string>(StringComparer.Ordinal) { selection.VoiceStableId },
            new HashSet<string>(StringComparer.Ordinal) { "MP3" },
            ConservativeCompiledEnvelopeLimitBytes)
            .Validate(preflight.CapturedAtUtc);
    }
}
