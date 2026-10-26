using System.Windows;
using AudioMemo.Core.Models;

namespace AudioMemo.Controls;

public static class ButtonVisuals
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.RegisterAttached(
        "Profile", typeof(string), typeof(ButtonVisuals), new PropertyMetadata(ButtonStyles.Windows));
    public static readonly DependencyProperty IsPrimaryProperty = DependencyProperty.RegisterAttached(
        "IsPrimary", typeof(bool), typeof(ButtonVisuals), new PropertyMetadata(false));

    public static string GetProfile(DependencyObject element) => (string)element.GetValue(ProfileProperty);
    public static void SetProfile(DependencyObject element, string value) => element.SetValue(ProfileProperty, value);
    public static bool GetIsPrimary(DependencyObject element) => (bool)element.GetValue(IsPrimaryProperty);
    public static void SetIsPrimary(DependencyObject element, bool value) => element.SetValue(IsPrimaryProperty, value);
}
