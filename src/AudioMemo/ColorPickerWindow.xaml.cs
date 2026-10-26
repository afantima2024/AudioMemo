using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class ColorPickerWindow : Window
{
    public string? SelectedColor { get; private set; }

    public ColorPickerWindow(Window? owner, string title, string initialColor, bool allowNeutral = false)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        NeutralButton.Visibility = allowNeutral ? Visibility.Visible : Visibility.Collapsed;
        foreach (var color in new[]
        {
            "#C8DDF5", "#C6E8D5", "#F1D2DC", "#E7D0F5", "#F5E0B3", "#D0E5EA", "#E8DFD4", "#FFFFFF",
            "#294B63", "#254F3D", "#66384C", "#523A6C", "#705928", "#22565F", "#51483F", "#202020"
        })
        {
            var button = new Button
            {
                Width = 44,
                Height = 28,
                MinHeight = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(2),
                Background = AppAppearance.Brush(RgbColor.Parse(color)),
                ToolTip = color
            };
            AutomationProperties.SetName(button, color);
            ButtonVisuals.SetProfile(button, ButtonStyles.Windows);
            button.Click += (_, _) => HexBox.Text = color;
            PresetsPanel.Children.Add(button);
        }
        HexBox.Text = RgbColor.Parse(initialColor).ToString();
    }

    private void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (UseColorButton is null)
            return;
        try
        {
            var color = RgbColor.Parse(HexBox.Text.Trim());
            ColorPreview.Background = AppAppearance.Brush(color);
            PreviewText.Foreground = AppAppearance.Brush(color.ReadableText);
            ColorStatus.Text = UiText.Get("AutomaticColorContrast");
            UseColorButton.IsEnabled = true;
        }
        catch (System.IO.InvalidDataException ex)
        {
            ColorStatus.Text = ex.Message;
            UseColorButton.IsEnabled = false;
        }
    }

    private void UseColor_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = RgbColor.Parse(HexBox.Text.Trim()).ToString();
        DialogResult = true;
    }

    private void Neutral_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = null;
        DialogResult = true;
    }
}
