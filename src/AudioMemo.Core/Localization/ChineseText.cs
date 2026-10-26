using System.Text;
using OpenccNetLib;

namespace AudioMemo.Core.Localization;

public static class ChineseText
{
    private static readonly Lazy<Opencc> Converter = new(() => new(OpenccConfig.T2S, isFrozen: true));

    public static string ToSimplified(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Ascii.IsValid(text) ? text : Converter.Value.Convert(text, punctuation: false);
    }
}
