using System.Windows;
using System.Windows.Input;
using AudioMemo.Controls;
using AudioMemo.Core.Models;

namespace AudioMemo;

public partial class MainWindow
{
    private FrameworkElement? ShortcutTarget(string id) => id switch
    {
        "new" => NewButton,
        "record" => StartButton,
        "stop" => StopButton,
        "settings" => AppSettingsMenu,
        "help" => AppHelpMenu,
        "library" => OpenLibraryButton,
        "files" => OpenSessionButton,
        "import" => ImportAudioButton,
        "recover" => RecoverButton,
        "refreshDevices" => RefreshDevicesButton,
        "model" => ApplyModelButton,
        "refreshModels" => RefreshModelButton,
        "live" => LiveTextCheck,
        "transcribe" => TranscribeButton,
        "speakers" => IdentifySpeakersButton,
        "restore" => RestoreSilenceButton,
        "cancelAi" => CancelAiButton,
        "summarize" => SummarizeButton,
        "dismissError" => WarningBanner.Visibility == Visibility.Visible ? DismissErrorButton : null,
        _ => TranscriptPanel.ShortcutTarget(id)
    };

    private void InitializeShortcuts()
    {
        PreviewKeyDown += (_, e) => HandleShortcut(e, false);
        TranscriptPanel.GlobalShortcutRequested += e => HandleShortcut(e, true);
        UpdateShortcutHints();
    }

    private void UpdateShortcutHints()
    {
        TranscriptPanel.ConfigureShortcuts(settings.Shortcuts);
        foreach (var command in ShortcutSettings.Commands)
            if (ShortcutTarget(command.Id) is { } target)
                ShortcutActions.SetHint(target, settings.Shortcuts, command.Id);
        ShortcutActions.SetHint(HistoryPeekTab, settings.Shortcuts, "history");
        ShortcutActions.SetHint(PinHistoryButton, settings.Shortcuts, "history");
        UpdateApplicationMenuShortcuts();
    }

    private void HandleShortcut(KeyEventArgs e, bool fromSearch)
    {
        if (!e.Handled)
            e.Handled = ExecuteShortcut(ShortcutInput.EventKey(e), Keyboard.Modifiers, fromSearch, e.IsRepeat);
    }

    private bool ExecuteShortcut(Key key, ModifierKeys modifiers, bool fromSearch, bool repeat)
    {
        if (closing || historyTabOrigin is not null || TranscriptPanel.HandlesNavigationPositionKey(key, modifiers))
            return false;
        var command = settings.Shortcuts.Find(ShortcutInput.Gesture(key, modifiers), TranscriptPanel.IsKeyboardFocusWithin, fromSearch);
        if (command is null)
            return false;
        if (repeat && command.Id is not ("pageUp" or "pageDown" or "searchNext" or "searchPrevious"))
            return true;
        if (command.Id == "history")
        {
            ToggleHistory_Click(this, new RoutedEventArgs());
            return true;
        }
        if (command.Id == "devices")
        {
            if (!initialized || !RefreshDevicesButton.IsEnabled)
                return false;
            _ = RunRecordingCommandAsync(ApplyDeviceChangesAsync);
            return true;
        }
        if (command.Id is "first" or "latest" or "pageUp" or "pageDown")
            return TranscriptPanel.NavigateTranscript(command.Id);
        if (command.Id is "transcriptPane" or "summaryPane" or "notesPane")
        {
            if (!PaneVisible(command.Id[..^4]))
                return false;
            ShowWorkspacePane(command.Id[..^4], focus: true);
            return true;
        }
        if (command.Id == "search")
            ShowWorkspacePane("transcript");
        return ShortcutActions.Invoke(ShortcutTarget(command.Id));
    }
}
