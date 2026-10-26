using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed class NotesSectionConflictException() : InvalidOperationException(UiText.Get("AboutMeSectionChanged"));

public sealed record NotesSectionUpdate(string Text, string Section, int Start, int RemovedLength, int InsertedLength)
{
    public int MapPosition(int position) => position <= Start ? position :
        position >= Start + RemovedLength ? position + InsertedLength - RemovedLength :
        Start + Math.Min(position - Start, InsertedLength);
}

public static class PersonalNotesSection
{
    public static bool ContainsOnce(string notes, string section) => Matches(notes, section).Count == 1;

    public static NotesSectionUpdate Update(string notes, string previousSection, string nextSection, bool appendIfChanged = false)
    {
        if (string.IsNullOrWhiteSpace(nextSection))
            throw new InvalidDataException(UiText.Get("InvalidAboutMeSummary"));
        var already = Matches(notes, nextSection);
        if (already.Count == 1)
            return new(notes, notes.Substring(already[0].Start, already[0].Length), 0, 0, 0);
        var newline = notes.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
            notes.Contains('\n') ? "\n" : Environment.NewLine;
        var section = nextSection.ReplaceLineEndings(newline).TrimEnd('\r', '\n');
        var previous = Matches(notes, previousSection);
        if (!appendIfChanged && already.Count == 0 && previous.Count == 1)
        {
            var match = previous[0];
            return new(notes.Remove(match.Start, match.Length).Insert(match.Start, section),
                section, match.Start, match.Length, section.Length);
        }
        if (!appendIfChanged && (previousSection.Length > 0 || already.Count > 1))
            throw new NotesSectionConflictException();
        var separator = notes.Length == 0 || notes.EndsWith(newline + newline, StringComparison.Ordinal) ? "" :
            notes.EndsWith('\n') || notes.EndsWith('\r') ? newline : newline + newline;
        var appended = separator + section;
        return new(notes + appended, section, notes.Length, 0, appended.Length);
    }

    private static List<(int Start, int Length)> Matches(string notes, string section)
    {
        var matches = new List<(int Start, int Length)>();
        if (section.Length == 0)
            return matches;
        foreach (var candidate in new[] { section, section.ReplaceLineEndings("\n"), section.ReplaceLineEndings("\r\n") }
            .Select(value => value.TrimEnd('\r', '\n')).Distinct(StringComparer.Ordinal))
        {
            if (candidate.Length == 0)
                continue;
            var start = 0;
            while (start <= notes.Length - candidate.Length &&
                notes.IndexOf(candidate, start, StringComparison.Ordinal) is var index && index >= 0)
            {
                var end = index + candidate.Length;
                if ((index == 0 || notes[index - 1] is '\r' or '\n') &&
                    (end == notes.Length || notes[end] is '\r' or '\n') &&
                    !matches.Any(match => match.Start == index))
                {
                    matches.Add((index, candidate.Length));
                    if (matches.Count == 2)
                        return matches;
                }
                start = index + candidate.Length;
            }
        }
        return matches;
    }
}
