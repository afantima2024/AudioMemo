using System.Security.Principal;
using System.Windows;
using AudioMemo.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    internal MessageHistoryStore? Messages { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() is "--speech-worker" or "--speech-server" or "--speaker-worker")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = await Task.Run(() => e.Args[0] switch
            {
                "--speech-server" => SpeechWorker.RunServerAsync(e.Args),
                "--speaker-worker" => SpeakerWorker.RunAsync(e.Args),
                _ => SpeechWorker.RunAsync(e.Args)
            });
            Shutdown(exitCode);
            return;
        }
        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var storage = new AppStorage();
            Messages = storage.Messages;
            var settings = new AppSettings();
            string? settingsLoadError = null;
            try { settings = storage.LoadSettings(); }
            catch (Exception ex) { settingsLoadError = ex.Message; }
            UiText.Current.ChangeLanguage(settings.UiLanguage);

            var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            instanceMutex = new Mutex(true, @"Local\AudioMemo-" + user, out ownsMutex);
            if (!ownsMutex)
            {
                MessageDialog.ShowMessage(null, UiText.Get("AlreadyRunning"), "AudioMemo",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            AppAppearance.Apply(this, settings);
            if (!settings.SetupCompleted)
            {
                if (settingsLoadError is not null)
                {
                    MessageDialog.ShowMessage(null, UiText.Format("SettingsLoadFailed", settingsLoadError),
                        UiText.Get("SettingsWindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    settingsLoadError = null;
                }
                var setup = new SetupWizardWindow(storage, settings);
                if (setup.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
                settings = setup.Settings;
                UiText.Current.ChangeLanguage(settings.UiLanguage);
                AppAppearance.Apply(this, settings);
            }
            var window = new MainWindow(storage, settings, settingsLoadError);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageDialog.ShowMessage(null, UiText.Format("StartupFailed", ex.Message), "AudioMemo",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex)
            instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
