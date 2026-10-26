using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioMemo.Core.Models;

namespace AudioMemo.Controls;

public static class ShortcutInput
{
    public static Key EventKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

    public static string Gesture(Key key, ModifierKeys modifiers)
    {
        var name = key switch { Key.Return => "Enter", Key.Prior => "PageUp", Key.Next => "PageDown", _ => key.ToString() };
        return string.Join("+", new[]
        {
            modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl" : null,
            modifiers.HasFlag(ModifierKeys.Alt) ? "Alt" : null,
            modifiers.HasFlag(ModifierKeys.Shift) ? "Shift" : null,
            modifiers.HasFlag(ModifierKeys.Windows) ? "Win" : null
        }.OfType<string>().Append(name));
    }

    public static bool Matches(ShortcutSettings settings, string id, Key key, ModifierKeys modifiers) =>
        settings.Gesture(id).Equals(Gesture(key, modifiers), StringComparison.Ordinal);

    public static string Display(string gesture) => string.Join("+", gesture.Split('+').Select(part =>
        part.Length == 2 && part[0] == 'D' && char.IsAsciiDigit(part[1]) ? part[1..] : part switch
        {
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemPlus" => "+",
            "OemMinus" => "-",
            "OemSemicolon" => ";",
            _ => part
        }));
}

public sealed class ShortcutCaptureBox : TextBox
{
    public ShortcutCaptureBox()
    {
        IsReadOnly = true;
        MaxLength = 80;
        MinWidth = 140;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (CaptureKey(ShortcutInput.EventKey(e), Keyboard.Modifiers))
            e.Handled = true;
        else
            base.OnPreviewKeyDown(e);
    }

    internal bool CaptureKey(Key key, ModifierKeys modifiers)
    {
        if (key is Key.Tab or Key.Escape)
            return false;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return true;
        SetCurrentValue(TextProperty, key is Key.Back or Key.Delete && modifiers == ModifierKeys.None
            ? "" : ShortcutInput.Gesture(key, modifiers));
        return true;
    }
}
