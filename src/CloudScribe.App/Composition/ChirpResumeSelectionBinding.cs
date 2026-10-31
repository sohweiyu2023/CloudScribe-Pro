using System.Security.Cryptography;
using System.Text;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public static class ChirpResumeSelectionBinding
{
    // A capture timestamp is freshness evidence, not stable request identity. No timestamp is rewritten for compilation.
    public static string Hash(ShellViewModel.GoogleTtsStudioRequestSelection selection) => ChirpPolicyValidation.Hash(new
    {
        Kind = "reserved-catalog-selection-v1",
        selection.DocumentId,
        selection.RevisionId,
        TextSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selection.ExactText))),
        selection.ProviderStableId,
        selection.AccountStableId,
        selection.ProjectStableId,
        selection.VoiceStableId,
        selection.CapabilityEvidenceId,
        selection.VoiceFingerprint,
        selection.LanguageCode,
        selection.Locale,
    });

    public static void RequireFresh(ShellViewModel.GoogleTtsStudioRequestSelection selection, DateTimeOffset now)
    {
        if (selection.CapturedAtUtc.Offset != TimeSpan.Zero || selection.CapturedAtUtc > now
            || now - selection.CapturedAtUtc > TimeSpan.FromMinutes(10))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("A fresh current UI selection capture is required for reserved request revalidation."), LocalRequestReason.SelectionChanged);
    }
}
