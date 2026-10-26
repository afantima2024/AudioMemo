using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

public sealed class AudioEditPlanTests
{
    [Fact]
    public void SeparateSpeakerSilencesPreserveDurationAndEveryRetainedTimestamp()
    {
        var source = Document(10,
            new(0, 1, "Keep the start.") { SpeakerId = "spkr1" },
            new(1, 2, "Remove one.") { SpeakerId = "spkr2" },
            new(2, 8, "Keep the middle.") { SpeakerId = "spkr1" },
            new(8, 9, "Remove two.") { SpeakerId = "spkr2" },
            new(9, 10, "Keep the end.") { SpeakerId = "spkr1" });
        source.SpeakerNames = new() { ["spkr1"] = "Alex", ["spkr2"] = "Noise" };
        var rows = TranscriptPresentation.Rows(source);
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [rows[3], rows[1], rows[1]]);
        Assert.Equal(2, plan.MutedRanges.Count);
        Assert.Equal(2L * PcmFormat.SampleRate, plan.MutedFrames);
        Assert.Equal(source.ProcessedFrames, plan.EditedFrames);
        var edited = plan.CreateTranscript();
        Assert.Equal([0d, 2d, 9d], edited.Segments.Select(segment => segment.StartSeconds));
        Assert.Equal([1d, 8d, 10d], edited.Segments.Select(segment => segment.EndSeconds));
        Assert.All(edited.Segments, segment => Assert.Equal("spkr1", segment.SpeakerId));
        Assert.Equal("Alex", Assert.Single(edited.SpeakerNames).Value);
        Assert.NotEqual(source.Revision, edited.Revision);
        Assert.False(edited.IsDraft);
        Assert.Equal(plan.EditedFrames, edited.ProcessedFrames);
        Assert.DoesNotContain("Remove", edited.ToText());
        Assert.Contains("Remove", source.ToText());
        Assert.True(plan.Matches(source));
        edited.Segments.Clear();
        Assert.Equal(3, plan.CreateTranscript().Segments.Count);
    }

    [Fact]
    public void AdjacentSilencesMergeWithoutMovingLaterText()
    {
        var source = Document(4, new(0, 1, "A."), new(1, 2, "B."), new(2, 4, "C."));
        var rows = TranscriptPresentation.Rows(source);
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [rows[1], rows[0]]);
        Assert.Equal(new AudioFrameRange(0, 2L * PcmFormat.SampleRate), Assert.Single(plan.MutedRanges));
        Assert.Equal(4L * PcmFormat.SampleRate, plan.EditedFrames);
        Assert.Equal(source.Segments[2], Assert.Single(plan.CreateTranscript().Segments));
    }

    [Fact]
    public void FrameRoundingDoesNotInventOverlapsAtDecimalBoundaries()
    {
        var source = Document(3, new(0, 1.1, "Cut."), new(1.1, 2.2, "Keep."));
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [TranscriptPresentation.Rows(source)[0]]);
        Assert.Equal(52_800, plan.MutedFrames);
        var segment = Assert.Single(plan.CreateTranscript().Segments);
        Assert.Equal(1.1, segment.StartSeconds);
        Assert.Equal(2.2, segment.EndSeconds);
    }

    [Fact]
    public void PartialOverlapsAreRejectedInsteadOfGuessingWords()
    {
        var source = Document(4,
            new(0, 2, "Voice one.") { SpeakerId = "spkr1" },
            new(1.5, 3, "Overlapping voice.") { SpeakerId = "spkr2" },
            new(3, 4, "Keep.") { SpeakerId = "spkr1" });
        var rows = TranscriptPresentation.Rows(source);
        Assert.Throws<InvalidOperationException>(() => AudioEditPlan.Create(source, source.ProcessedFrames, [rows[0]]));
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [rows[0], rows[1]]);
        Assert.Equal(3L * PcmFormat.SampleRate, plan.MutedFrames);
        Assert.Equal("Keep.", Assert.Single(plan.CreateTranscript().Segments).Text);
    }

    [Fact]
    public void FullyOverlappedTextIsRemovedWhenItsAudioIsSilenced()
    {
        var source = Document(4,
            new(0, 2, "Cut.") { SpeakerId = "spkr1" },
            new(0.5, 1.5, "Also inside the cut.") { SpeakerId = "spkr2" },
            new(2, 4, "Keep.") { SpeakerId = "spkr1" });
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [TranscriptPresentation.Rows(source)[0]]);
        Assert.Equal(2, plan.RemovedSegmentCount);
        Assert.Equal("Keep.", Assert.Single(plan.CreateTranscript().Segments).Text);
    }

    [Fact]
    public void PlansRejectStaleSelectionsAndDraftsButAllowAllAudioToBeSilenced()
    {
        var source = Document(2, new(0, 1, "One."), new(1, 2, "Two."));
        var rows = TranscriptPresentation.Rows(source);
        Assert.Throws<InvalidOperationException>(() => AudioEditPlan.Create(source, source.ProcessedFrames, []));
        var all = AudioEditPlan.Create(source, source.ProcessedFrames, rows);
        Assert.Equal(source.ProcessedFrames, all.EditedFrames);
        Assert.Equal(source.ProcessedFrames, all.MutedFrames);
        Assert.Empty(all.CreateTranscript().Segments);
        Assert.Throws<InvalidOperationException>(() => AudioEditPlan.Create(source, source.ProcessedFrames,
            [rows[0] with { Text = "Old selection." }]));
        source.IsDraft = true;
        Assert.Throws<InvalidOperationException>(() => AudioEditPlan.Create(source, source.ProcessedFrames, [rows[0]]));
    }

    [Fact]
    public void FingerprintDetectsContentChangesEvenWithoutARevisionChange()
    {
        var source = Document(2, new(0, 1, "One."), new(1, 2, "Two."));
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [TranscriptPresentation.Rows(source)[0]]);
        source.Segments[1] = source.Segments[1] with { Text = "Changed externally." };
        Assert.False(plan.Matches(source));
    }

    [Fact]
    public void TimelineSupportsLongRecordingsAndDisplaysHoursBeyondOneDay()
    {
        var source = Document(26 * 3600, new(0, 1, "Start."), new(25 * 3600, 25 * 3600 + 2, "Remove late."));
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [TranscriptPresentation.Rows(source)[1]]);
        Assert.Equal(source.ProcessedFrames, plan.EditedFrames);
        Assert.Equal(2L * PcmFormat.SampleRate, plan.MutedFrames);
        Assert.Equal("25:00:00.000", AudioEditPlan.FormatTime(25L * 3600 * PcmFormat.SampleRate));
    }

    [Fact]
    public void GroupedRowsKeepTheFullEndWhenSegmentsOverlap()
    {
        var source = Document(4, new(0, 3, "A continuing"), new(1, 2, "phrase"));
        Assert.Equal(3, Assert.Single(TranscriptPresentation.Rows(source)).EndSeconds);
    }

    [Fact]
    public void KeptAudioSpansCrossingCopyBuffersRemainFrameExact()
    {
        using var folder = new TestFolder();
        var wave = folder.WriteTone(12);
        var source = Document(12, new(0, 1, "First."), new(1, 2, "Cut."), new(2, 12, "Last."));
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [TranscriptPresentation.Rows(source)[1]]);
        var result = folder.File("edited.wav");
        using (var input = File.OpenRead(wave))
            AudioSilenceWriter.Write(input, result, plan, null, CancellationToken.None);
        var originalBytes = File.ReadAllBytes(wave);
        var editedBytes = File.ReadAllBytes(result);
        Assert.Equal(originalBytes.Length, editedBytes.Length);
        Assert.Equal(originalBytes.AsSpan(DurableWaveFile.HeaderSize, PcmFormat.BytesPerSecond).ToArray(),
            editedBytes.AsSpan(DurableWaveFile.HeaderSize, PcmFormat.BytesPerSecond).ToArray());
        Assert.Equal(originalBytes.AsSpan(DurableWaveFile.HeaderSize + 2 * PcmFormat.BytesPerSecond).ToArray(),
            editedBytes.AsSpan(DurableWaveFile.HeaderSize + 2 * PcmFormat.BytesPerSecond).ToArray());
        Assert.All(editedBytes.AsSpan(DurableWaveFile.HeaderSize + PcmFormat.BytesPerSecond, PcmFormat.BytesPerSecond).ToArray(),
            value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void UnmutedTranscriptTimesAreNotRoundedToSampleBoundaries()
    {
        var source = Document(3, new(0, 1, "Silence."), new(1.123456789, 2.987654321, "Keep exact times."));
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, [TranscriptPresentation.Rows(source)[0]]);
        Assert.Equal(source.Segments[1], Assert.Single(plan.CreateTranscript().Segments));
    }

    [Fact]
    public void FullySilentOutputSpansMultipleBuffersAndRetainsTheCompleteWaveHeader()
    {
        using var folder = new TestFolder();
        var wave = folder.WriteTone(12);
        var source = Document(12, new TranscriptSegment(0, 12, "Silence all."));
        var plan = AudioEditPlan.Create(source, source.ProcessedFrames, TranscriptPresentation.Rows(source));
        var result = folder.File("all-silent.wav");
        using (var input = File.OpenRead(wave))
            AudioSilenceWriter.Write(input, result, plan, null, CancellationToken.None);
        var original = File.ReadAllBytes(wave);
        var edited = File.ReadAllBytes(result);
        Assert.Equal(original.Length, edited.Length);
        Assert.Equal(original[..DurableWaveFile.HeaderSize], edited[..DurableWaveFile.HeaderSize]);
        Assert.False(edited.AsSpan(DurableWaveFile.HeaderSize).ContainsAnyExcept((byte)0));
    }

    internal static TranscriptDocument Document(double seconds, params TranscriptSegment[] segments) => new()
    {
        ProcessedFrames = (long)(seconds * PcmFormat.SampleRate),
        Model = "base",
        Language = "en",
        Revision = "original-revision",
        SpeakersIdentified = true,
        Segments = segments.ToList()
    };
}
