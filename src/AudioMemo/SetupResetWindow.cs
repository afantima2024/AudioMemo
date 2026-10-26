using System.Windows;
using System.Windows.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo;

internal sealed class SetupResetWindow : Window
{
    private readonly RadioButton keep;
    internal SetupResetMode Choice => keep.IsChecked == true ? SetupResetMode.KeepSettings : SetupResetMode.ResetPreferences;

    public SetupResetWindow(Window owner)
    {
        Owner = owner;
        Title = UiText.Get("SetupRunAgain");
        Width = 590;
        Height = 390;
        MinWidth = 380;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var root = new DockPanel { Margin = new Thickness(22) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var proceed = new Button { Content = UiText.Get("SetupContinue"), IsDefault = true, MinWidth = 90 };
        proceed.Click += (_, _) => DialogResult = true;
        actions.Children.Add(proceed);
        actions.Children.Add(new Button { Content = UiText.Get("Cancel"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = UiText.Get("SetupResetHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        keep = new RadioButton
        {
            Name = "KeepSettingsChoice", IsChecked = true, GroupName = "setup",
            Content = new TextBlock { Text = UiText.Get("SetupKeepSettings"), TextWrapping = TextWrapping.Wrap },
            Margin = new Thickness(0, 0, 0, 14)
        };
        body.Children.Add(keep);
        body.Children.Add(new RadioButton
        {
            Name = "ResetPreferencesChoice", GroupName = "setup",
            Content = new TextBlock { Text = UiText.Get("SetupResetPreferences"), TextWrapping = TextWrapping.Wrap }
        });
        body.Children.Add(new TextBlock { Text = UiText.Get("SetupResetPreservesFiles"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
    }
}
