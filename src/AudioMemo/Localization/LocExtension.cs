using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using AudioMemo.Core.Localization;

namespace AudioMemo.Localization;

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]")
        {
            Source = UiText.Current,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}

public sealed class SessionStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            "Recording" => UiText.Get("SessionRecording"),
            "Paused" => UiText.Get("SessionPaused"),
            "Saved" => UiText.Get("SessionSaved"),
            "Interrupted" => UiText.Get("SessionInterrupted"),
            "Recovered" => UiText.Get("SessionRecovered"),
            _ => value
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
