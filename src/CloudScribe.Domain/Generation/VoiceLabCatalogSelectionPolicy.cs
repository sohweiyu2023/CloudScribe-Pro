namespace CloudScribe.Domain.Generation;

public static class VoiceLabCatalogSelectionPolicy
{
    public static VoiceLabCatalogSelection RequireEligible(VoiceLabCatalogSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Validate();
    }
}
