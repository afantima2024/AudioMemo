using System.Text.Json.Serialization;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed record SelfSpeaker(string SpeakerId, List<string> Names)
{
    [JsonRequired] public string TagId { get; init; } = Guid.NewGuid().ToString("N");

    public SelfSpeaker Copy() => new(SpeakerId, [.. Names]) { TagId = TagId };

    public bool Matches(SelfSpeaker? other) => other is not null && SpeakerId == other.SpeakerId &&
        TagId == other.TagId && Names.SequenceEqual(other.Names, StringComparer.Ordinal);

    public static SelfSpeaker Create(string speakerId, string names)
    {
        var result = new SelfSpeaker(speakerId, names.ReplaceLineEndings("\n").Split('\n')
            .Select(name => ChineseText.ToSimplified(name.Trim())).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SpeakerId) || SpeakerId.Length > 80 || SpeakerId.Any(char.IsControl) ||
            !Guid.TryParseExact(TagId, "N", out _) || Names is null || Names.Count > 10 ||
            Names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)))
            throw new InvalidDataException(UiText.Get("InvalidMeTag"));
    }
}
