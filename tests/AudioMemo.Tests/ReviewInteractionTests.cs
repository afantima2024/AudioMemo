using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

public sealed class ReviewInteractionTests
{
    [Fact]
    public void SpeakerFilteringUsesIdsAndNeverMergesAcrossHiddenVoices()
    {
        var document = new TranscriptDocument
        {
            Segments =
            [
                new(0, 1, "First thought") { SpeakerId = "spkr1" },
                new(1, 1.5, "An interruption.") { SpeakerId = "spkr2" },
                new(1.5, 3, "continuing.") { SpeakerId = "spkr1" },
                new(3, 4, "Uncertain speaker.")
            ]
        };
        var filter = new SpeakerFilter();
        Assert.Equal(4, filter.Apply(TranscriptPresentation.Rows(document)).Count);
        filter.Select(["spkr1"]);
        var filtered = filter.Apply(TranscriptPresentation.Rows(document));
        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain("interruption", TranscriptPresentation.CopyText(filtered));
        document.RenameSpeaker("spkr1", "Alex");
        Assert.All(filter.Apply(TranscriptPresentation.Rows(document)), row => Assert.Equal("Alex", row.Speaker));
        filter.Select(["spkr2", null]);
        Assert.Equal(2, filter.Apply(TranscriptPresentation.Rows(document)).Count);
        filter.Select([]);
        Assert.Empty(filter.Apply(TranscriptPresentation.Rows(document)));
        filter.ShowAll();
        Assert.Equal(4, filter.Apply(TranscriptPresentation.Rows(document)).Count);
        Assert.Equal(4, document.Segments.Count);
    }

    [Fact]
    public void PeekWaitsFiveSecondsAfterExitAndActivityRestartsTheCountdown()
    {
        var state = new HistoryPeekState();
        state.Reveal(TimeSpan.Zero);
        state.UpdatePointer(true, TimeSpan.Zero);
        Assert.False(state.Expire(TimeSpan.FromHours(2)));
        state.UpdatePointer(false, TimeSpan.FromHours(2));
        Assert.False(state.Expire(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(4.999)));
        state.Interact(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(4));
        Assert.False(state.Expire(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(8.999)));
        Assert.True(state.Expire(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(9)));
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void ReenteringPeekKeepsItVisibleAndASecondExitGetsAFullCountdown()
    {
        var state = new HistoryPeekState();
        state.Reveal(TimeSpan.Zero);
        state.UpdatePointer(false, TimeSpan.Zero);
        state.UpdatePointer(true, TimeSpan.FromSeconds(4));
        Assert.False(state.Expire(TimeSpan.FromSeconds(10)));
        state.UpdatePointer(false, TimeSpan.FromSeconds(20));
        Assert.False(state.Expire(TimeSpan.FromSeconds(24.9)));
        Assert.True(state.Expire(TimeSpan.FromSeconds(25)));
        state.Reveal(TimeSpan.FromSeconds(30));
        Assert.True(state.IsOpen);
        state.Close();
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void ReplayRangeIsContinuousAndClipsOnlyAtTheSavedAudioEnd()
    {
        var range = PlaybackRange.FromRows([
            new TranscriptRow(8, 10, "spkr2", "Other", "Last"),
            new TranscriptRow(2, 3, "spkr1", "First", "First")]);
        Assert.Equal(new PlaybackRange(2, 10), range);
        Assert.Equal((2L * PcmFormat.SampleRate, 8L * PcmFormat.SampleRate), range.ToFrames(12L * PcmFormat.SampleRate));
        Assert.Equal((2L * PcmFormat.SampleRate, 3L * PcmFormat.SampleRate), range.ToFrames(5L * PcmFormat.SampleRate));
        Assert.Equal((12L, 24L), new PlaybackRange(0.00025, 0.00075).ToFrames(PcmFormat.SampleRate));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(3, 2)]
    [InlineData(2, 2)]
    [InlineData(double.NaN, 2)]
    [InlineData(1, double.PositiveInfinity)]
    public void ReplayRejectsInvalidTimes(double start, double end) =>
        Assert.Throws<InvalidDataException>(() => new PlaybackRange(start, end).ToFrames(PcmFormat.SampleRate));

    [Fact]
    public void ReplayRejectsEmptySelectionsAndUnsavedTimes()
    {
        Assert.Throws<InvalidOperationException>(() => PlaybackRange.FromRows([]));
        Assert.Throws<InvalidOperationException>(() => new PlaybackRange(5, 8).ToFrames(5L * PcmFormat.SampleRate));
        Assert.Throws<InvalidOperationException>(() => new PlaybackRange(0, 1).ToFrames(0));
    }
}
