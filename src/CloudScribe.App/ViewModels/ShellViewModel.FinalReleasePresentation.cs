using CloudScribe.App.Navigation;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    /// <summary>
    /// Replaces historical staged-build route copy with truthful release-state copy.
    /// This method intentionally does not relax any provider, pricing, spend, trust,
    /// media-validation, or recovery gate. A fresh installation therefore remains
    /// fail-closed until the corresponding current evidence exists.
    /// </summary>
    public void ApplyFinalReleasePresentation()
    {
        RoutePageViewModel studio = _pages[AppRoute.Studio];
        studio.StateTitle = "Local workspace ready";
        studio.StateDescription =
            "Create or import a local document to begin. Billable generation remains unavailable until an eligible provider, current pricing evidence and explicit authorization are present.";
        studio.StateKind = "READY";
        studio.Detail = $"Provider adapters available to the application: {_providerRegistry.AvailableProviders.Count:N0} · no provider is selected implicitly";

        RoutePageViewModel library = _pages[AppRoute.Library];
        library.StateTitle = "Local document library";
        library.StateDescription =
            "Documents, autosaves and explicit checkpoints stay on this device unless you explicitly choose an export or provider operation.";
        library.StateKind = "LOCAL";

        RoutePageViewModel queue = _pages[AppRoute.Queue];
        queue.StateTitle = "No generation jobs";
        queue.StateDescription =
            "No generation work is queued. Billable submission stays fail-closed until the document, provider, pricing and authorization evidence for that exact request are valid.";
        queue.StateKind = "EMPTY";

        RoutePageViewModel audio = _pages[AppRoute.Audio];
        audio.StateTitle = "No verified local audio";
        audio.StateDescription =
            "Playback and audition actions appear only when the corresponding verified local media or trusted Voice Lab capability is available.";
        audio.StateKind = "EMPTY";

        RoutePageViewModel pricing = _pages[AppRoute.Pricing];
        pricing.StateTitle = "No active trusted pricing catalog";
        pricing.StateDescription =
            "Pricing inspection is available, but billable approval remains blocked until exact trusted control material is admitted and the required account/capability evidence is current.";
        pricing.StateKind = "BLOCKED";
        pricing.Detail =
            "No active pricing catalog · activation is never automatic · provider discovery is never implicit";
    }
}
