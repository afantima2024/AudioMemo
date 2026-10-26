using System.Windows;
using System.Windows.Controls;
using AudioMemo.Core.Localization;

namespace AudioMemo;

public sealed class TextInputWindow : Window
{
    private readonly TextBox input;
    public string Value => input.Text.Trim();

    public TextInputWindow(Window? owner, string title, string value, int maximumLength, bool allowEmpty = false,
        bool selectAll = true)
    {
        Owner = owner;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 12) });
        input = new TextBox { Text = value, MaxLength = maximumLength };
        input.Select(selectAll ? 0 : value.Length, selectAll ? value.Length : 0);
        panel.Children.Add(input);
        if (allowEmpty)
            panel.Children.Add(new TextBlock { Text = UiText.Get("AutomaticTitleHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button { Content = UiText.Get("SaveChanges"), IsDefault = true };
        save.Click += (_, _) =>
        {
            if (allowEmpty || Value.Length > 0)
                DialogResult = true;
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = UiText.Get("Cancel"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            input.Focus();
            input.Select(selectAll ? 0 : input.Text.Length, selectAll ? input.Text.Length : 0);
        };
    }
}
