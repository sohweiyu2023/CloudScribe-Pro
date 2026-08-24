using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public static class GoogleGenerationUiTrustBindingPolicy
{
    public static void RequireExactBinding(
        GoogleGenerationUiSelection selection,
        GenerationCacheTrustContext admittedTrust)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(admittedTrust);
        admittedTrust.Validate();

        RequireEqual(selection.AccountId, admittedTrust.AccountId, "account");
        RequireEqual(selection.ProjectId, admittedTrust.ProjectId, "project");
        RequireEqual(selection.VoiceId, admittedTrust.VoiceStableId, "voice");
        RequireEqual(selection.ModelId, admittedTrust.ResolvedModelId, "model");
        RequireEqual(selection.CapabilityEvidenceId, admittedTrust.CapabilityIdentity, "capability evidence");
        RequireEqual(selection.OutputFormat, admittedTrust.OutputFormat, "output format");
    }

    private static void RequireEqual(string uiValue, string admittedValue, string identityName)
    {
        if (!string.Equals(uiValue, admittedValue, StringComparison.Ordinal))
            throw new InvalidOperationException($"Google UI {identityName} identity differs from the admitted v2.23 generation trust context.");
    }
}
