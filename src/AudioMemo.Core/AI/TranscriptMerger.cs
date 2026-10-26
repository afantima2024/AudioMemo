using System.Text.RegularExpressions;
using AudioMemo.Core.Models;

namespace AudioMemo.Core.AI;

public static class TranscriptMerger
{
    public static void Append(TranscriptDocument document, IReadOnlyList<TranscriptSegment> segments,
        double offsetSeconds, double previousBoundary, double endSeconds)
    {
        foreach (var result in segments)
        {
            var start = Math.Max(0, offsetSeconds + result.StartSeconds);
            var end = Math.Min(endSeconds, offsetSeconds + result.EndSeconds);
            if (end <= previousBoundary || end <= start)
                continue;
            var text = result.Text.Trim();
            if (start < previousBoundary && document.Segments.LastOrDefault() is { } previous)
                text = RemoveRepeatedPrefix(previous.Text, text);
            if (text.Length == 0)
                continue;
            document.Segments.Add(new(Math.Max(start, previousBoundary), end, text));
        }
    }

    public static string RemoveRepeatedPrefix(string previous, string current)
    {
        var oldWords = Regex.Matches(previous, @"\S+");
        var newWords = Regex.Matches(current, @"\S+");
        for (var count = Math.Min(40, Math.Min(oldWords.Count, newWords.Count)); count >= 2; count--)
        {
            var match = true;
            for (var index = 0; index < count && match; index++)
                match = string.Equals(oldWords[oldWords.Count - count + index].Value,
                    newWords[index].Value, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                var last = newWords[count - 1];
                return current[(last.Index + last.Length)..].TrimStart();
            }
        }
        if (oldWords.Count == 1 && newWords.Count == 1)
        {
            for (var count = Math.Min(80, Math.Min(previous.Length, current.Length)); count >= 3; count--)
                if (previous.AsSpan(previous.Length - count).SequenceEqual(current.AsSpan(0, count)))
                    return current[count..];
        }
        return current;
    }
}
