using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace AudioMemo.Core.Localization;

public sealed record LanguageOption(string Code, string Label)
{
    public override string ToString() => Label;
}

public sealed class UiText : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new(
        "AudioMemo.Core.Localization.Strings", typeof(UiText).Assembly);
    private CultureInfo culture;

    public static UiText Current { get; } = new();
    public static IReadOnlyList<LanguageOption> InterfaceLanguages { get; } =
    [
        new("en", "English"),
        new("zh-CN", "\u7b80\u4f53\u4e2d\u6587")
    ];

    public UiText(string language = "en") => culture = GetCulture(language);

    public event PropertyChangedEventHandler? PropertyChanged;
    public CultureInfo Culture => culture;
    public string this[string key] => Resources.GetString(key, culture)
        ?? throw new InvalidOperationException($"Missing interface translation: {key}");

    public static bool IsSupportedLanguage(string? language) => language is "en" or "zh-CN";
    public static string Get(string key) => Current[key];
    public static string Format(string key, params object?[] arguments) =>
        Current.FormatValue(key, arguments);

    public string FormatValue(string key, params object?[] arguments) =>
        string.Format(culture, this[key], arguments);

    public void ChangeLanguage(string language)
    {
        var next = GetCulture(language);
        if (next.Name == culture.Name)
            return;
        culture = next;
        PropertyChanged?.Invoke(this, new(nameof(Culture)));
        PropertyChanged?.Invoke(this, new("Item[]"));
    }

    private static CultureInfo GetCulture(string language) =>
        IsSupportedLanguage(language)
            ? CultureInfo.GetCultureInfo(language)
            : throw new ArgumentException("Unsupported interface language.", nameof(language));
}
