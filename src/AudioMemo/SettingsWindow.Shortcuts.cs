using System.ComponentModel;
using System.IO;
using System.Windows;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo;

public partial class SettingsWindow
{
    private IReadOnlyList<ShortcutRow> shortcutRows = [];
    private bool shortcutsValid = true;

    private void InitializeShortcuts()
    {
        shortcutRows = ShortcutSettings.Commands.Select(command => new ShortcutRow(command, Settings.Shortcuts.Gesture(command.Id))).ToArray();
        ShortcutEditGrid.ItemsSource = shortcutRows;
        foreach (var row in shortcutRows)
            row.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(ShortcutRow.Gesture))
                    return;
                try
                {
                    CaptureShortcuts().Validate();
                    shortcutsValid = true;
                    ShortcutStatus.Text = UiText.Get("ShortcutChangesPending");
                }
                catch (InvalidDataException ex)
                {
                    shortcutsValid = false;
                    ShortcutStatus.Text = UiText.Format("ErrorDetails", ex.Message);
                }
                SaveButton.ToolTip = shortcutsValid ? null : UiText.Get("ShortcutNeedsCorrection");
                UpdateModelOperationControls();
            };
    }

    private ShortcutSettings CaptureShortcuts() => new()
    {
        Overrides = shortcutRows.Where(row => row.Gesture != row.Command.DefaultGesture)
            .ToDictionary(row => row.Command.Id, row => row.Gesture)
    };

    private void ReadShortcuts()
    {
        var next = CaptureShortcuts();
        next.Validate();
        Settings.Shortcuts = next;
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in shortcutRows)
            row.Gesture = row.Command.DefaultGesture;
    }

    private sealed class ShortcutRow(ShortcutCommand command, string gesture) : INotifyPropertyChanged
    {
        public ShortcutCommand Command { get; } = command;
        public string Label => UiText.Get(Command.LabelKey);
        public string Scope => UiText.Get(Command.TranscriptOnly ? "ShortcutTranscriptScope" : "ShortcutWindowScope");
        public string DisplayGesture => Gesture.Length == 0 ? UiText.Get("ShortcutUnassigned") : ShortcutInput.Display(Gesture);
        public string Gesture
        {
            get => gesture;
            set
            {
                if (gesture == value)
                    return;
                gesture = value;
                PropertyChanged?.Invoke(this, new(nameof(Gesture)));
                PropertyChanged?.Invoke(this, new(nameof(DisplayGesture)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
