using System.Text.RegularExpressions;

namespace AudioMemo.Core.Models;

public static partial class SummaryPresentation
{
    public static string ForDisplay(string text)
    {
        var result = new List<string>();
        var insideTimedNote = false;
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var match = TimestampNote().Match(line);
            if (!match.Success)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    insideTimedNote = false;
                result.Add(insideTimedNote ? "  " + line.Trim() : line);
                continue;
            }
            insideTimedNote = true;
            result.Add(match.Groups["time"].Value.Trim());
            var note = match.Groups["note"].Value.Trim();
            if (note.Length > 0)
                result.Add("  " + note);
        }
        return string.Join(Environment.NewLine, result);
    }

    public static string WithoutRecordingTimestamps(string text)
    {
        var result = new List<string>();
        foreach (var original in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var match = TimestampNote().Match(original);
            var line = BracketedTimestamp().Replace(match.Success ? match.Groups["note"].Value.Trim() : original, "").TrimEnd();
            if ((match.Success || line != original.TrimEnd()) &&
                string.IsNullOrWhiteSpace(line.Trim().Trim('-', '*', '+')))
                continue;
            result.Add(line);
        }
        return string.Join(Environment.NewLine, result).Trim();
    }

    public static string AppendOverall(string mainPoints, string overall, string heading) =>
        mainPoints.TrimEnd() + Environment.NewLine + Environment.NewLine +
        "# " + heading + Environment.NewLine + Environment.NewLine + overall;

    [GeneratedRegex(@"[\[(]\d{2,}:\d{2}:\d{2}(?:\s*(?:-|\u2013|\u2014|to|\u81f3)\s*\d{2,}:\d{2}:\d{2})?[\])]",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex BracketedTimestamp();

    [GeneratedRegex(@"^\s*(?:[-*+]\s+|\d+[.)]\s+)?(?:\*\*)?[\[(]?(?<time>\d{2,}:\d{2}:\d{2}(?:\s*(?:-|\u2013|\u2014|to|\u81f3)\s*\d{2,}:\d{2}:\d{2})?)[\])]?(?:\*\*)?[\s:\uFF1A-]*(?<note>.*)$",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex TimestampNote();
}
