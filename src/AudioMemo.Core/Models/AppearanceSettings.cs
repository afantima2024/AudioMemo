using System.Globalization;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public static class ButtonStyles
{
    public const string Windows = "windows";
    public const string Office = "office";
    public const string Shadow = "shadow";
    public const string Outline = "outline";

    public static bool IsSupported(string? value) => value is Windows or Office or Shadow or Outline;

    public static IReadOnlyList<LanguageOption> Options(UiText text) =>
    [
        new(Windows, text["ButtonsWindows"]),
        new(Office, text["ButtonsOffice"]),
        new(Shadow, text["ButtonsShadow"]),
        new(Outline, text["ButtonsOutline"])
    ];
}

public sealed class HighlightColors
{
    public string Selection { get; set; } = "#C8DDF5";
    public string Match { get; set; } = "#FFE8A3";
    public string ActiveMatch { get; set; } = "#FFD166";

    public void Validate()
    {
        RgbColor.Parse(Selection);
        RgbColor.Parse(Match);
        RgbColor.Parse(ActiveMatch);
    }
}

public sealed class AppearanceSettings
{
    public string ButtonStyle { get; set; } = ButtonStyles.Windows;
    public bool RainbowRecordingIndicator { get; set; }
    public HighlightColors Light { get; set; } = new();
    public HighlightColors Dark { get; set; } = new()
    {
        Selection = "#294B63",
        Match = "#5F4A12",
        ActiveMatch = "#80610D"
    };
    public ComparisonColors LightComparison { get; set; } = new();
    public ComparisonColors DarkComparison { get; set; } = new()
    {
        Different = "#522D3F",
        Matching = "#244838",
        Unclear = "#3B414B"
    };

    public void Validate()
    {
        if (!ButtonStyles.IsSupported(ButtonStyle))
            throw new InvalidDataException(UiText.Get("InvalidButtonStyle"));
        if (Light is null || Dark is null || LightComparison is null || DarkComparison is null)
            throw new InvalidDataException(UiText.Get("InvalidAppearanceColors"));
        Light.Validate();
        Dark.Validate();
        LightComparison.Validate();
        DarkComparison.Validate();
    }
}

public sealed class ComparisonColors
{
    public string Different { get; set; } = "#FCE4EC";
    public string Matching { get; set; } = "#E4F3E8";
    public string Unclear { get; set; } = "#E4E7EB";

    public void Validate()
    {
        RgbColor.Parse(Different);
        RgbColor.Parse(Matching);
        RgbColor.Parse(Unclear);
    }
}

public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public static RgbColor Parse(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException(UiText.Get("InvalidHexColor"));
        return new((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public override string ToString() => $"#{Red:X2}{Green:X2}{Blue:X2}";

    public double Luminance
    {
        get
        {
            static double Channel(byte value)
            {
                var fraction = value / 255d;
                return fraction <= 0.04045 ? fraction / 12.92 : Math.Pow((fraction + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(Red) + 0.7152 * Channel(Green) + 0.0722 * Channel(Blue);
        }
    }

    public double Contrast(RgbColor other) =>
        (Math.Max(Luminance, other.Luminance) + 0.05) / (Math.Min(Luminance, other.Luminance) + 0.05);

    public RgbColor ReadableText => Contrast(new(0, 0, 0)) >= Contrast(new(255, 255, 255))
        ? new(0, 0, 0) : new(255, 255, 255);

    public RgbColor ForText(RgbColor foreground)
    {
        if (Contrast(foreground) >= 4.5)
            return this;
        var target = foreground.Contrast(new(0, 0, 0)) >= foreground.Contrast(new(255, 255, 255))
            ? new RgbColor(0, 0, 0) : new RgbColor(255, 255, 255);
        for (var step = 1; step <= 255; step++)
        {
            byte Blend(byte current, byte end) => (byte)Math.Round(current + (end - current) * step / 255d);
            var adjusted = new RgbColor(Blend(Red, target.Red), Blend(Green, target.Green), Blend(Blue, target.Blue));
            if (adjusted.Contrast(foreground) >= 4.5)
                return adjusted;
        }
        return target;
    }
}
