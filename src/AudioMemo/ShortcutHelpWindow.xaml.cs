using System.Windows;
using System.Windows.Markup;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo;

public partial class ShortcutHelpWindow : Window
{
    public ShortcutHelpWindow(ShortcutSettings shortcuts)
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(UiText.Current.Culture.Name);
        ShortcutHelpGrid.ItemsSource = ShortcutSettings.Commands.Select(command => new
        {
            command.Id,
            Label = UiText.Get(command.LabelKey),
            Scope = UiText.Get(command.TranscriptOnly ? "ShortcutTranscriptScope" : "ShortcutWindowScope"),
            DisplayGesture = shortcuts.Gesture(command.Id) is { Length: > 0 } gesture
                ? ShortcutInput.Display(gesture) : UiText.Get("ShortcutUnassigned")
        }).ToArray();
    }
}
