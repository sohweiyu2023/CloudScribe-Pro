namespace CloudScribe.App.Composition;

public sealed record WalkthroughStep(WalkthroughScreen Screen, string Target, string Heading, string Instruction,
    string? Link = null);
