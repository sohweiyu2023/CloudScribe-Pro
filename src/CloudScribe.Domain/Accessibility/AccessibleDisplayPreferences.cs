namespace CloudScribe.Domain.Accessibility;

public enum AccessibleContrastPreference
{
    System = 0,
    Normal = 1,
    High = 2,
}

public sealed record AccessibleDisplayPreferences(
    double TextScale,
    AccessibleContrastPreference Contrast,
    bool ReduceMotion,
    bool PreferVisibleFocus,
    bool AnnounceDynamicChanges)
{
    public AccessibleDisplayPreferences Validate()
    {
        if (double.IsNaN(TextScale) || double.IsInfinity(TextScale) || TextScale is < 1.0 or > 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(TextScale), "Accessible text scale must be between 100% and 200%.");
        }

        if (!Enum.IsDefined(Contrast))
        {
            throw new ArgumentOutOfRangeException(nameof(Contrast));
        }

        return this;
    }

    public static AccessibleDisplayPreferences Default =>
        new(1.0, AccessibleContrastPreference.System, false, true, true);
}
