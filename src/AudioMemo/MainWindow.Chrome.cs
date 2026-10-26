using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Shell;
using AudioMemo.Core.Localization;

namespace AudioMemo;

public partial class MainWindow
{
    private void WindowHeader_SizeChanged(object sender, SizeChangedEventArgs e) =>
        WindowChrome.GetWindowChrome(this).CaptionHeight = Math.Max(40, e.NewSize.Height);

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateWindowButtons();

    private void UpdateWindowButtons()
    {
        if (MaximizeWindowButton is null)
            return;
        var maximized = WindowState == WindowState.Maximized;
        var label = UiText.Get(maximized ? "WindowRestore" : "WindowMaximize");
        MaximizeWindowButton.ToolTip = label;
        AutomationProperties.SetName(MaximizeWindowButton, label);
        MaximizeWindowGlyph.Data = Geometry.Parse(maximized
            ? "M2.5,0.5 L9.5,0.5 9.5,7.5 M0.5,2.5 L7.5,2.5 7.5,9.5 0.5,9.5 Z"
            : "M0.5,0.5 L9.5,0.5 9.5,9.5 0.5,9.5 Z");
    }
}
