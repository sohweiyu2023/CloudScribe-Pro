using CloudScribe.App.Navigation;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    /// <summary>
    /// Replaces historical staged-build route and seeded-document copy with truthful release-state copy.
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
        queue.StateTitle = "Persisted request inspection";
        queue.StateDescription =
            "Open Setups in the toolbar for persisted request counts and unresolved states. This page does not claim the durable queue is empty. An uncertain dispatched request may have been charged; do not resubmit until its provider outcome is known.";
        queue.StateKind = "LOCAL";

        RoutePageViewModel audio = _pages[AppRoute.Audio];
        audio.StateTitle = "Saved MP3 audio";
        audio.StateDescription =
            "Open the saved MP3 folder to listen to existing audio without generating again. Current-session Play/Export verify accepted bytes. Older files remain on disk but are not silently adopted as trusted cache entries.";
        audio.StateKind = "LOCAL";

        RoutePageViewModel pricing = _pages[AppRoute.Pricing];
        pricing.StateTitle = "No active trusted pricing catalog";
        pricing.StateDescription =
            "Pricing inspection is available, but billable approval remains blocked until exact trusted control material is admitted and the required account/capability evidence is current.";
        pricing.StateKind = "BLOCKED";
        pricing.Detail =
            "No active pricing catalog · activation is never automatic · provider discovery is never implicit";

        // The Stage 2 seed document was originally useful as a shell demonstrator, but it becomes
        // actively misleading in a Final build because it says generation and durable workflows
        // are future-stage work. Keep a local primer, while describing the release as it actually
        // behaves: local editing is ready and network/billable work remains evidence-gated.
        DocumentTitle = "CloudScribe Pro Local Workspace";
        DocumentText = string.Join(Environment.NewLine,
        [
            "CloudScribe Pro is a local-first reading and production studio for long-form text-to-speech work.",
            string.Empty,
            "Create or import documents locally, use durable autosave and checkpoints, and keep editing available without provider access.",
            string.Empty,
            "Provider-backed generation is available only through the production safety gates. CloudScribe does not implicitly choose an account, trust a pricing catalog, discover credentials, or authorize billable work.",
            string.Empty,
            "When current provider, account, project, pricing, trust, credential, spend and reconciliation evidence are all valid for the exact request, the corresponding production action can become available. Missing or stale evidence keeps that operation blocked.",
            string.Empty,
            "Focus Reading removes surrounding production controls without replacing the editor. Press F11 to enter or leave Focus Reading, Ctrl+Shift+O for the outline, Ctrl+Shift+I for the inspector, Ctrl+Shift+Q for the queue, and Ctrl+/ for the shortcut guide.",
        ]);

        RebuildDocumentOutline(DocumentText);
    }
}
