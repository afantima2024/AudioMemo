using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public static class AppThemes
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    public static bool IsSupported(string? value) => value is Light or Dark or System;

    public static IReadOnlyList<LanguageOption> Options(UiText text) =>
    [
        new(Light, text["ThemeLight"]),
        new(Dark, text["ThemeDark"]),
        new(System, text["ThemeSystem"])
    ];
}
