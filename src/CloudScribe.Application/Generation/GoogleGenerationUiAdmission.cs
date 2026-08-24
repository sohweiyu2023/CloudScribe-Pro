namespace CloudScribe.Application.Generation;

public sealed record GoogleGenerationUiSelection(
    string AccountId,
    string ProjectId,
    string VoiceId,
    string ModelId,
    string CapabilityEvidenceId,
    string OutputFormat);

public static class GoogleGenerationUiAdmission
{
    public static GoogleGenerationUiSelection RequireCurrent(
        GoogleGenerationUiSelection selection,
        bool accountAuthorized,
        bool projectAuthorized,
        bool capabilityCurrent,
        bool pricingCurrent)
    {
        ArgumentNullException.ThrowIfNull(selection);
        foreach (var value in new[] { selection.AccountId, selection.ProjectId, selection.VoiceId, selection.ModelId, selection.CapabilityEvidenceId, selection.OutputFormat })
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("Google generation UI selection contains a non-canonical identity.");
        }
        if (!accountAuthorized || !projectAuthorized)
            throw new InvalidOperationException("Google generation UI selection is not authorized for the current account/project.");
        if (!capabilityCurrent)
            throw new InvalidOperationException("Google generation capability evidence is stale.");
        if (!pricingCurrent)
            throw new InvalidOperationException("Google generation pricing evidence is stale.");
        return selection;
    }
}
