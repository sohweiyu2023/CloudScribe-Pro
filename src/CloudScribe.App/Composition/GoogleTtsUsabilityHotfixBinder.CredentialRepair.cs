using Avalonia.Controls;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private async Task OpenCredentialRepairAsync(Window owner, ShellViewModel model, TextBlock status)
    {
        if (credentialRepair is null) { status.Text = "Credential repair is unavailable in this build."; return; }
        try
        {
            var configured = (await accounts.ListAsync().ConfigureAwait(true))
                .Where(x => x.IsEnabled && string.Equals(x.Reference.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal)).ToArray();
            if (configured.Length != 1) throw new InvalidOperationException("Select exactly one existing Google account.");
            status.Text = await credentialRepair.ReadStatusAsync(configured[0].Reference.AccountId).ConfigureAwait(true);
            var dialog = new GoogleCredentialRepairDialog(credentialRepair, configured[0].Reference.AccountId);
            model.BeginGoogleCredentialRepair();
            try { await dialog.ShowDialog(owner).ConfigureAwait(true); }
            finally
            {
                if (!dialog.MutationAttempted) model.EndGoogleCredentialRepairWithoutMutation();
                status.Text = await credentialRepair.ReadStatusAsync(configured[0].Reference.AccountId).ConfigureAwait(true);
            }
        }
        catch (Exception error) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(error))
        { status.Text = "Repair could not be opened or confirmed. Existing connection/history retained; inspect local repair status. No speech requested."; }
    }
}
