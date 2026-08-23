using CloudScribe.Domain.Accessibility;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8AccessibleDisplayPreferencesTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void SupportedTextScalesValidate(double scale)
    {
        var preferences = new AccessibleDisplayPreferences(scale, AccessibleContrastPreference.High, true, true, true);

        Assert.Same(preferences, preferences.Validate());
    }

    [Theory]
    [InlineData(0.99)]
    [InlineData(2.01)]
    public void UnsupportedTextScalesFailClosed(double scale)
    {
        var preferences = new AccessibleDisplayPreferences(scale, AccessibleContrastPreference.System, false, true, true);

        Assert.Throws<ArgumentOutOfRangeException>(() => preferences.Validate());
    }

    [Fact]
    public void DefaultsPreserveVisibleFocusAndDynamicAnnouncements()
    {
        var preferences = AccessibleDisplayPreferences.Default.Validate();

        Assert.True(preferences.PreferVisibleFocus);
        Assert.True(preferences.AnnounceDynamicChanges);
        Assert.Equal(1.0, preferences.TextScale);
    }
}
