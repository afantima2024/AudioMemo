using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Controls;

internal static class ShortcutActions
{
    public static bool Invoke(FrameworkElement? target)
    {
        if (target is null || !target.IsEnabled || target.Visibility != Visibility.Visible)
            return false;
        if (target is ToggleButton toggle)
            toggle.IsChecked = toggle.IsChecked != true;
        if (target is ButtonBase button)
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        else if (target is MenuItem menu)
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        else
            return false;
        return true;
    }

    public static string Hint(ShortcutSettings settings, string id)
    {
        var command = ShortcutSettings.Commands.Single(item => item.Id == id);
        var key = id switch
        {
            "devices" => "DevicesHint",
            "play" => "ReplayHint",
            "silence" => "MuteSelectedRowsHint",
            "edit" => "CorrectSelectionHint",
            "import" => "ImportAudioHint",
            "export" => "ExportWholeRecordingHint",
            "latest" => "TranscriptBottomHint",
            "online" => "OnlineSelectionHint",
            "search" => "SearchWindowHint",
            "speakerFilter" => "SpeakerFilterHint",
            "speakerColor" => "SpeakerColorHint",
            _ => command.LabelKey
        };
        var gesture = settings.Gesture(id);
        return UiText.Get(key) + (gesture.Length == 0 ? "" : Environment.NewLine + ShortcutInput.Display(gesture));
    }

    public static void SetHint(FrameworkElement target, ShortcutSettings settings, string id)
    {
        target.ToolTip = Hint(settings, id);
        if (target is MenuItem menu)
            menu.InputGestureText = ShortcutInput.Display(settings.Gesture(id));
    }
}
