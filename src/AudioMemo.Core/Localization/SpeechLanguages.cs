namespace AudioMemo.Core.Localization;

public static class SpeechLanguages
{
    public static IReadOnlyList<LanguageOption> GetOptions(UiText text, bool compact = false) =>
    [
        new("auto", text[compact ? "SpeechLanguageAutomaticShort" : "SpeechLanguageAutomatic"]),
        new("zh", text[compact ? "SpeechLanguageChineseShort" : "SpeechLanguageChinese"]),
        new("en", text[compact ? "SpeechLanguageEnglishShort" : "SpeechLanguageEnglish"])
    ];

    public static string ParseInput(string value, UiText text)
    {
        var input = value.Trim();
        if (input.Length == 0)
            throw new ArgumentException(text["InvalidSpeechLanguage"], nameof(value));
        var code = GetOptions(text).FirstOrDefault(option =>
            option.Label.Equals(input, StringComparison.OrdinalIgnoreCase))?.Code
            ?? input.ToLowerInvariant();
        return code is "auto" or "zh" or "en"
            ? code
            : throw new ArgumentException(text["InvalidSpeechLanguage"], nameof(value));
    }
}
