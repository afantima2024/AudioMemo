namespace AudioMemo.Core.Models;

public sealed class SpeakerFilter
{
    private HashSet<string?>? selected;

    public bool IsAll => selected is null;
    public bool Includes(string? speakerId) => selected is null || selected.Contains(speakerId);
    public void ShowAll() => selected = null;
    public void Select(IEnumerable<string?> speakerIds) => selected = speakerIds.ToHashSet();
    public IReadOnlyList<TranscriptRow> Apply(IEnumerable<TranscriptRow> rows) =>
        rows.Where(row => Includes(row.SpeakerId)).ToArray();
}
