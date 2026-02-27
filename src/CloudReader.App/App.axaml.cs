using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CloudReader.App.ViewModels;
using CloudReader.App.Views;
using CloudReader.GoogleTts.Services;
using V1 = Google.Cloud.TextToSpeech.V1;
using V1Beta1 = Google.Cloud.TextToSpeech.V1Beta1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CloudReader.App;

public partial class App : Application
{
    private IHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(static services =>
            {
                services.AddSingleton(_ => V1.TextToSpeechClient.Create());
                services.AddSingleton(_ => V1Beta1.TextToSpeechLongAudioSynthesizeClient.Create());
                services.AddSingleton<IGoogleTtsClient, GoogleTtsClient>();
                services.AddSingleton<MainWindowViewModel>();
            })
            .Build();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _host.Services.GetRequiredService<MainWindowViewModel>()
            };

            desktop.Exit += (_, _) => _host.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
