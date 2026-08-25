namespace CloudScribe.Domain.Accessibility;

public sealed record AccessibleDisplayPreferences(
    double TextScale,
    AccessibleContrastPreference Contrast,
    bool ReduceMotion,
    bool PreferVisibleFocus,
    bool AnnounceDynamicChanges)
{
    public AccessibleDisplayPreferences Validate() => Validate(TextScale, Contrast);

    private AccessibleDisplayPreferences Validate(
        double textScale,
        AccessibleContrastPreference contrast)
    {
        if (double.IsNaN(textScale) || double.IsInfinity(textScale) || textScale is < 1.0 or > 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(textScale), "Accessible text scale must be between 100% and 200%.");
        }

        if (!Enum.IsDefined(contrast))
        {
            throw new ArgumentOutOfRangeException(nameof(contrast));
        }

        return this;
    }

    public static AccessibleDisplayPreferences Default =>
        new(1.0, AccessibleContrastPreference.System, false, true, true);
}
