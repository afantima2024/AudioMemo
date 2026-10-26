using System.Text;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed record TranscriptMatch(int RowIndex, int Start, int Length, int Distance = 0)
{
    public int End => Start + Length;
}

public static class TranscriptSearch
{
    public const int MaximumQueryLength = 128;

    public static IReadOnlyList<TranscriptMatch> Find(IReadOnlyList<TranscriptRow> rows, string query, bool fuzzy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Length > MaximumQueryLength)
            throw new ArgumentException(UiText.Get("SearchQueryTooLong"), nameof(query));
        query = ChineseText.ToSimplified(query.Trim());
        if (query.Length == 0)
            return [];
        var pattern = query.EnumerateRunes().Select(Rune.ToUpperInvariant).ToArray();
        var tolerance = fuzzy && pattern.Length >= 3 ? pattern.Length >= 8 ? 2 : 1 : 0;
        var matches = new List<TranscriptMatch>();
        for (var row = 0; row < rows.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tolerance == 0)
            {
                var text = rows[row].Text;
                for (var start = 0; start <= text.Length - query.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var found = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                    if (found < 0)
                        break;
                    matches.Add(new(row, found, query.Length));
                    start = found + query.Length;
                }
            }
            else
                FindApproximate(rows[row].Text, row, pattern, tolerance, matches, cancellationToken);
        }
        return matches;
    }

    private static void FindApproximate(string text, int row, Rune[] pattern, int tolerance,
        List<TranscriptMatch> matches, CancellationToken cancellationToken)
    {
        var letters = text.EnumerateRunes().ToArray();
        var offsets = new int[letters.Length + 1];
        for (var index = 0; index < letters.Length; index++)
        {
            offsets[index + 1] = offsets[index] + letters[index].Utf16SequenceLength;
            letters[index] = Rune.ToUpperInvariant(letters[index]);
        }
        var previous = new Cell[pattern.Length + 1];
        var older = new Cell[pattern.Length + 1];
        var current = new Cell[pattern.Length + 1];
        for (var index = 0; index <= pattern.Length; index++)
            previous[index] = new(index, 0);
        TranscriptMatch? pending = null;
        var acceptedEnd = 0;
        for (var index = 1; index <= letters.Length; index++)
        {
            if ((index & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            current[0] = new(0, index);
            for (var column = 1; column <= pattern.Length; column++)
            {
                var cost = letters[index - 1] == pattern[column - 1] ? 0 : 1;
                var best = previous[column - 1].Add(cost);
                best = Choose(best, previous[column].Add(1), index, column);
                best = Choose(best, current[column - 1].Add(1), index, column);
                if (index > 1 && column > 1 && letters[index - 1] == pattern[column - 2] &&
                    letters[index - 2] == pattern[column - 1])
                    best = Choose(best, older[column - 2].Add(1), index, column);
                current[column] = best;
            }
            var hit = current[^1];
            if (hit.Distance <= tolerance)
            {
                var first = hit.Start;
                var last = index;
                while (first < last && Rune.IsWhiteSpace(letters[first]))
                    first++;
                while (last > first && Rune.IsWhiteSpace(letters[last - 1]))
                    last--;
                if (first < last && offsets[first] >= acceptedEnd)
                {
                    var candidate = new TranscriptMatch(row, offsets[first], offsets[last] - offsets[first], hit.Distance);
                    if (pending is not null && candidate.Start >= pending.End)
                    {
                        matches.Add(pending);
                        acceptedEnd = pending.End;
                        pending = null;
                    }
                    if (pending is null || candidate.Distance < pending.Distance ||
                        (candidate.Distance == pending.Distance && candidate.Length > pending.Length))
                        pending = candidate;
                    // Publish exact hits immediately so adjacent/repeated occurrences stay distinct.
                    if (pending.Distance == 0)
                    {
                        matches.Add(pending);
                        acceptedEnd = pending.End;
                        pending = null;
                    }
                }
            }
            (older, previous, current) = (previous, current, older);
        }
        if (pending is not null)
            matches.Add(pending);
    }

    private static Cell Choose(Cell left, Cell right, int end, int columns)
    {
        if (right.Distance < left.Distance || (right.Distance == left.Distance &&
            Math.Abs(end - right.Start - columns) < Math.Abs(end - left.Start - columns)))
            return right;
        return left;
    }

    private readonly record struct Cell(int Distance, int Start)
    {
        public Cell Add(int distance) => new(Distance + distance, Start);
    }
}
