using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public static class VoiceCatalogRefreshPresentation
{
    // UiQuery catches provider/cancellation errors itself and filter changes return early.
    // The outer owned refresh still ends unsuccessfully, so do not forward ordinary retry advice.
    public static bool RejectIncomplete(ShellViewModel model, bool restrictedRefresh, string ordinaryMessage)
    {
        string message = restrictedRefresh
            ? "Diagnostic voice verification did not complete. The run may be stopped or awaiting reconciliation. Review its recorded state before another attempt. Existing audio and reservations are retained; no automatic retry was made."
            : ordinaryMessage;
        model.ConnectedVoiceCatalogStatus = message;
        model.StatusMessage = message;
        return false;
    }
}
