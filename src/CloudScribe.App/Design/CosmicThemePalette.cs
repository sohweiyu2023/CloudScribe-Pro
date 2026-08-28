using Avalonia.Media;

namespace CloudScribe.App.Design;

public sealed record CosmicThemePalette
{
    public required Color Perimeter { get; init; }

    public required Color PerimeterSoft { get; init; }

    public required Color Surface { get; init; }

    public required Color SurfaceRaised { get; init; }

    public required Color SurfaceInset { get; init; }

    public required Color SurfaceHover { get; init; }

    public required Color SurfaceHighlight { get; init; }

    public required Color Paper { get; init; }

    public required Color PaperWarm { get; init; }

    public required Color PaperInset { get; init; }

    public required Color PaperBorder { get; init; }

    public required Color Ink { get; init; }

    public required Color InkMuted { get; init; }

    public required Color TextOnDark { get; init; }

    public required Color TextOnDarkMuted { get; init; }

    public required Color Primary { get; init; }

    public required Color PrimaryBright { get; init; }

    public required Color PrimaryFillBright { get; init; }

    public required Color PrimaryText { get; init; }

    public required Color Secondary { get; init; }

    public required Color SecondaryBright { get; init; }

    public required Color Cyan { get; init; }

    public required Color CyanBright { get; init; }

    public required Color Border { get; init; }

    public required Color BorderSubtle { get; init; }

    public required Color Focus { get; init; }

    public required Color Success { get; init; }

    public required Color SuccessSoft { get; init; }

    public required Color Warning { get; init; }

    public required Color WarningSoft { get; init; }

    public required Color WarningOnPaper { get; init; }

    public required Color Error { get; init; }

    public required Color ErrorSoft { get; init; }

    public required Color Info { get; init; }

    public required Color InfoSoft { get; init; }

    public required Color Selection { get; init; }

    public required Color Scrim { get; init; }

    public required Color AmbientViolet { get; init; }

    public required Color AmbientBlue { get; init; }

    public required bool PreferDarkControls { get; init; }

    public required bool DecorativeEffectsEnabled { get; init; }
}
