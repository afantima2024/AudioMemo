using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Audio;

public static class AudioRangeSet
{
    public static IReadOnlyList<AudioFrameRange> Normalize(IEnumerable<AudioFrameRange> ranges, long frames)
    {
        var result = new List<AudioFrameRange>();
        foreach (var range in ranges.OrderBy(range => range.StartFrame))
        {
            if (range.StartFrame < 0 || range.EndFrame <= range.StartFrame || range.EndFrame > frames)
                throw new InvalidDataException(UiText.Get("InvalidEditTimeline"));
            if (result.Count > 0 && range.StartFrame <= result[^1].EndFrame)
                result[^1] = result[^1] with { EndFrame = Math.Max(result[^1].EndFrame, range.EndFrame) };
            else
                result.Add(range);
        }
        return result.AsReadOnly();
    }

    public static IReadOnlyList<AudioFrameRange> Subtract(IEnumerable<AudioFrameRange> ranges,
        IEnumerable<AudioFrameRange> removed, long frames)
    {
        var source = Normalize(ranges, frames);
        var cuts = Normalize(removed, frames);
        var result = new List<AudioFrameRange>();
        var index = 0;
        foreach (var range in source)
        {
            var position = range.StartFrame;
            while (index < cuts.Count && cuts[index].EndFrame <= position)
                index++;
            var current = index;
            while (current < cuts.Count && cuts[current].StartFrame < range.EndFrame)
            {
                if (cuts[current].StartFrame > position)
                    result.Add(new(position, cuts[current].StartFrame));
                position = Math.Max(position, cuts[current].EndFrame);
                if (position >= range.EndFrame)
                    break;
                current++;
            }
            if (position < range.EndFrame)
                result.Add(new(position, range.EndFrame));
        }
        return result.AsReadOnly();
    }

    public static bool Overlaps(IReadOnlyList<AudioFrameRange> ranges, long first, long last)
    {
        var index = FirstEndingAfter(ranges, first);
        return index < ranges.Count && (first == last
            ? first >= ranges[index].StartFrame : ranges[index].StartFrame < last);
    }

    public static int FirstEndingAfter(IReadOnlyList<AudioFrameRange> ranges, long frame)
    {
        var lower = 0;
        var upper = ranges.Count;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (ranges[middle].EndFrame <= frame)
                lower = middle + 1;
            else
                upper = middle;
        }
        return lower;
    }
}
