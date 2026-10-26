using System.Text;
using System.Text.RegularExpressions;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed record TranscriptRow(double StartSeconds, double EndSeconds, string? SpeakerId, string Speaker, string Text)
{
    public string Time => TranscriptDocument.Timestamp(StartSeconds);
    public string MeLabel { get; init; } = "";
    public string DisplaySpeaker => SpeakerId is null ? UiText.Get("UnlabeledSpeakers") :
        MeLabel.Length == 0 ? Speaker : $"{Speaker} ({MeLabel})";
}

public sealed record TranscriptGroup(TranscriptRow Row, IReadOnlyList<int> SegmentIndices);

public static class TranscriptPresentation
{
    public static IReadOnlyList<TranscriptRow> Rows(TranscriptDocument document) =>
        BuildRows(document, includeIndices: false, static (row, _) => row).ToArray();

    public static IReadOnlyList<TranscriptGroup> Groups(TranscriptDocument document) =>
        BuildRows(document, includeIndices: true, static (row, indices) => new TranscriptGroup(row, indices));

    private static List<T> BuildRows<T>(TranscriptDocument document, bool includeIndices,
        Func<TranscriptRow, IReadOnlyList<int>, T> create)
    {
        var result = new List<T>();
        TranscriptRow? current = null;
        StringBuilder? combined = null;
        List<int>? indices = includeIndices ? [] : null;
        double endSeconds = 0;
        for (var index = 0; index < document.Segments.Count; index++)
        {
            var segment = document.Segments[index];
            var text = segment.Text.Trim();
            if (text.Length == 0)
                continue;
            var length = combined is { Length: > 0 } ? combined.Length : current?.Text.Length ?? 0;
            if (current is not null &&
                current.SpeakerId == segment.SpeakerId && segment.StartSeconds - endSeconds <= 1.5 &&
                length + text.Length < 600 && !EndsSentence(combined is { Length: > 0 } ? combined[^1] : current.Text[^1]))
            {
                combined ??= new StringBuilder(600);
                if (combined.Length == 0)
                    combined.Append(current.Text);
                if (NeedsSpace(combined[^1], text[0]))
                    combined.Append(' ');
                combined.Append(text);
                endSeconds = Math.Max(endSeconds, segment.EndSeconds);
            }
            else
            {
                if (current is not null)
                    FinishGroup(current);
                combined?.Clear();
                indices?.Clear();
                current = new(segment.StartSeconds, segment.EndSeconds, segment.SpeakerId,
                    segment.SpeakerId is null ? "" : document.SpeakerName(segment.SpeakerId), text)
                {
                    MeLabel = segment.SpeakerId is not null && document.Me?.SpeakerId == segment.SpeakerId
                        ? UiText.Get("MeTag") : ""
                };
                endSeconds = segment.EndSeconds;
            }
            indices?.Add(index);
        }
        if (current is not null)
            FinishGroup(current);
        return result;

        void FinishGroup(TranscriptRow row) => result.Add(create(
            combined is { Length: > 0 } ? row with { EndSeconds = endSeconds, Text = combined.ToString() } : row,
            indices is null ? [] : [.. indices]));
    }

    public static string CopyText(IEnumerable<TranscriptRow> rows, bool annotated = false) =>
        string.Join(Environment.NewLine, rows.Select(row => annotated
            ? $"[{row.Time}] " + (row.Speaker.Length == 0 ? "" : row.Speaker + ": ") + row.Text
            : row.Text));

    public static string RenderSpeakerReferences(string text, TranscriptDocument document)
    {
        Dictionary<string, string>? names = null;
        return Regex.Replace(text, @"\[spkr[1-9][0-9]*\]", match =>
        {
            var reference = match.Value;
            names ??= new(StringComparer.Ordinal);
            if (names.TryGetValue(reference, out var name))
                return name;
            var id = reference[1..^1];
            name = document.Segments.Any(segment => segment.SpeakerId == id) ? document.SpeakerName(id) : reference;
            names.Add(reference, name);
            return name;
        });
    }

    private static bool EndsSentence(char last) => ".!?。\uFF01\uFF1F".Contains(last);
    private static bool NeedsSpace(char left, char right) =>
        !(IsCjk(left) && IsCjk(right)) && !"，。！？、；：,.!?;:)】）".Contains(right);
    private static bool IsCjk(char value) => value is >= '\u2e80' and <= '\u9fff' or >= '\uff00' and <= '\uffef';
}
