using System.Windows.Media;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Controls;

public static class SpeakerColors
{
    public static Brush ForSpeaker(string? id, IReadOnlyDictionary<string, string> colors, bool highContrast,
        string? unlabeledColor = null)
    {
        var color = id is null ? unlabeledColor : colors.GetValueOrDefault(id);
        return !highContrast && color is not null ? AppAppearance.Brush(RgbColor.Parse(color)) : Brushes.Transparent;
    }
}
