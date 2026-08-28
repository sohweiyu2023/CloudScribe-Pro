namespace CloudScribe.App.Design;

public sealed record AdaptiveLayoutState(
    AdaptiveLayoutMode Mode,
    bool ShowNavigationRail,
    bool ShowOutlinePanel,
    bool ShowInspectorPanel,
    bool ShowCompactCommandLabels,
    double EditorMaximumWidth)
{
    public const double CompactBreakpoint = 800;
    public const double StandardBreakpoint = 1100;
    public const double FullBreakpoint = 1440;

    public bool IsNarrow => Mode == AdaptiveLayoutMode.Narrow;

    public bool IsCompact => Mode == AdaptiveLayoutMode.Compact;

    public bool IsStandard => Mode == AdaptiveLayoutMode.Standard;

    public bool IsFull => Mode == AdaptiveLayoutMode.Full;

    public static AdaptiveLayoutState ForWidth(double width)
    {
        if (!double.IsFinite(width))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "Viewport width must be finite.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(width);

        if (width < CompactBreakpoint)
        {
            return new(AdaptiveLayoutMode.Narrow, false, false, false, true, 760);
        }

        if (width < StandardBreakpoint)
        {
            return new(AdaptiveLayoutMode.Compact, true, false, false, true, 800);
        }

        if (width < FullBreakpoint)
        {
            return new(AdaptiveLayoutMode.Standard, true, true, false, false, 820);
        }

        return new(AdaptiveLayoutMode.Full, true, true, true, false, 860);
    }
}
