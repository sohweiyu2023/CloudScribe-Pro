namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private int _googleCredentialRepairActive;
    private readonly System.Threading.Lock _googleCredentialOperationGate = new();

    public void BeginGoogleCredentialRepair()
    {
        lock (_googleCredentialOperationGate)
        {
            if (Volatile.Read(ref _googleGenerationInFlight) != 0 || Volatile.Read(ref _voiceLabAuditionInFlight) != 0)
                throw new InvalidOperationException("Wait for generation and Voice Lab auditions to finish before credential repair.");
            Interlocked.Exchange(ref _googleCredentialRepairActive, 1);
        }
        Interlocked.Increment(ref _googleRequestVersion);
        ClearChangedGoogleRequest();
        SelectedVoiceLabVoice = null;
        VoiceLabCatalogResults.Clear();
        RefreshGoogleGenerationCommands();
        OnPropertyChanged(nameof(CanAuditionSelectedVoice));
        AuditionSelectedVoiceCommand.NotifyCanExecuteChanged();
    }

    public void EndGoogleCredentialRepairWithoutMutation()
    {
        lock (_googleCredentialOperationGate) Interlocked.Exchange(ref _googleCredentialRepairActive, 0);
        RefreshGoogleGenerationCommands();
        OnPropertyChanged(nameof(CanAuditionSelectedVoice));
        AuditionSelectedVoiceCommand.NotifyCanExecuteChanged();
    }
}
