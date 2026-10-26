using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Controls;

public sealed class ColorPickerButton : Button
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color),
        typeof(string), typeof(ColorPickerButton), new PropertyMetadata("#FFFFFF", ColorChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label),
        typeof(string), typeof(ColorPickerButton), new PropertyMetadata(""));

    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public ColorPickerButton()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        SetBinding(AutomationProperties.NameProperty, new Binding(nameof(Label)) { Source = this });
        UpdatePreview();
    }

    private static void ColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((ColorPickerButton)sender).UpdatePreview();

    private void UpdatePreview()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var swatch = new Border
        {
            Width = 24,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Background = AppAppearance.Brush(RgbColor.Parse(Color)),
            Margin = new Thickness(0, 0, 8, 0)
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "AppDivider");
        var label = new TextBlock { Text = Color, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        row.Children.Add(swatch);
        row.Children.Add(label);
        Content = row;
        ToolTip = Color;
    }

    protected override void OnClick()
    {
        base.OnClick();
        var dialog = new ColorPickerWindow(Window.GetWindow(this), Label, Color);
        if (dialog.ShowDialog() == true && dialog.SelectedColor is { } color)
            Color = color;
    }
}
