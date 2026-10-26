using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo;

public partial class MainWindow
{
    private readonly Action<HelpWindow> showHelp;

    private void AppMenu_Click(object sender, RoutedEventArgs e)
    {
        if (closing)
            return;
        UpdateControls();
        AppMenu.PlacementTarget = AppMenuButton;
        AppMenu.Placement = PlacementMode.Bottom;
        AppMenu.IsOpen = true;
    }

    private void AppMenu_Opened(object sender, RoutedEventArgs e) => UpdateControls();

    private void AppCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!closing && sender is MenuItem { Tag: string command })
            ShortcutActions.Invoke(ShortcutTarget(command));
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (closing)
            return;
        try { showHelp(new HelpWindow(settings.Shortcuts)); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateApplicationMenu()
    {
        if (AppMenu is null)
            return;
        AppMenuButton.IsEnabled = AppMenu.IsEnabled = !closing;
        AppHistoryMenu.IsEnabled = AppMainPointsMenu.IsEnabled = AppMyNotesMenu.IsEnabled =
            AppMessageHistoryMenu.IsEnabled = AppHelpMenu.IsEnabled = !closing;
        if (closing)
            AppMenu.IsOpen = false;
        AppRecordMenu.Header = StartButton.Content;
        AppRecordMenu.IsEnabled = StartButton.IsEnabled;
        AppStopMenu.IsEnabled = StopButton.IsEnabled;
        AppNewRecordingMenu.IsEnabled = NewButton.IsEnabled;
        AppImportMenu.IsEnabled = ImportAudioButton.IsEnabled;
        AppOpenFolderMenu.IsEnabled = !closing && OpenLibraryButton.IsEnabled;
        AppHistoryMenu.Header = UiText.Get(HistoryPinned || CompactHistory && historyPeek.IsOpen
            ? "HideRecordingList" : CompactHistory ? "ShowRecordingList" : "PinRecordingList");
        AppMainPointsMenu.IsChecked = settings.MainPointsVisible;
        AppMainPointsMenu.Header = UiText.Get(settings.MainPointsVisible ? "HideMainPointsMenu" : "ShowMainPoints");
        AppMyNotesMenu.IsChecked = settings.MyNotesVisible;
        AppMyNotesMenu.Header = UiText.Get(settings.MyNotesVisible ? "HideMyNotesMenu" : "ShowMyNotes");
    }

    private void UpdateApplicationMenuShortcuts()
    {
        foreach (var item in AppMenu.Items.OfType<MenuItem>())
            if (item.Tag is string id && ShortcutSettings.Commands.Any(command => command.Id == id))
                ShortcutActions.SetHint(item, settings.Shortcuts, id);
    }
}
