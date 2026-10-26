using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

public sealed class AudioRestorePlanTests
{
    [Fact]
    public void PartialRestorationKeepsCorrectionsNamesAndUnselectedSilence()
    {
        var original = Original();
        var current = AudioEditPlan.ForMutedRanges(original, original.ProcessedFrames, Muted()).CreateTranscript();
        current.Segments[0] = current.Segments[0] with { Text = "A corrected retained sentence." };
        current.RenameSpeaker("spkr1", "Jamie");
        var plan = new AudioRestorePlan(current, original, original.ProcessedFrames, Muted(), [Muted()[0]]);
        var restored = plan.CreateTranscript();
        Assert.False(plan.RebuildsTranscript);
        Assert.Equal(original.ProcessedFrames, restored.ProcessedFrames);
        Assert.Equal(Muted()[1], Assert.Single(plan.RemainingMutedRanges));
        Assert.Equal("A corrected retained sentence.", restored.Segments[0].Text);
        Assert.Equal([0d, 1d, 2d], restored.Segments.Select(segment => segment.StartSeconds));
        Assert.Equal("Jamie", restored.SpeakerNames["spkr1"]);
        Assert.Equal("Original voice", restored.SpeakerNames["spkr2"]);
        Assert.Equal(original.Segments[1], restored.Segments[1]);
        Assert.DoesNotContain(original.Segments[3], restored.Segments);
        Assert.NotEqual(current.Revision, restored.Revision);
    }

    [Fact]
    public void ChangedModelOrSpeakerAssignmentsRequireAnExplicitTranscriptRebuildWarning()
    {
        var original = Original();
        var current = AudioEditPlan.ForMutedRanges(original, original.ProcessedFrames, Muted()).CreateTranscript();
        current.Model = "small";
        current.Segments[0] = current.Segments[0] with { Text = "Later recognition changed this." };
        var plan = new AudioRestorePlan(current, original, original.ProcessedFrames, Muted(), Muted());
        Assert.True(plan.RebuildsTranscript);
        Assert.Equal(original.Segments, plan.CreateTranscript().Segments);
        Assert.Equal("Later recognition changed this.", current.Segments[0].Text);
        current.Model = original.Model;
        current.Segments[0] = current.Segments[0] with { SpeakerId = "spkr2" };
        Assert.True(new AudioRestorePlan(current, original, original.ProcessedFrames, Muted(), Muted()).RebuildsTranscript);
    }

    [Fact]
    public void AllSilentSessionsCanRecoverEverythingAtTheOriginalTimes()
    {
        var original = Original();
        AudioFrameRange[] all = [new(0, original.ProcessedFrames)];
        var current = AudioEditPlan.ForMutedRanges(original, original.ProcessedFrames, all).CreateTranscript();
        Assert.Empty(current.Segments);
        var plan = new AudioRestorePlan(current, original, original.ProcessedFrames, all, all);
        Assert.False(plan.RebuildsTranscript);
        Assert.Equal(original.Segments, plan.CreateTranscript().Segments);
        Assert.Empty(plan.RemainingMutedRanges);
        Assert.Equal(original.ProcessedFrames, plan.RestoredFrames);
    }

    [Fact]
    public void PartialRestoreNeverGuessesTextAcrossAnUnrestoredBoundary()
    {
        var original = Original();
        var current = AudioEditPlan.ForMutedRanges(original, original.ProcessedFrames, Muted()).CreateTranscript();
        original.Segments[2] = original.Segments[2] with { EndSeconds = 3.5 };
        Assert.Throws<InvalidOperationException>(() =>
            new AudioRestorePlan(current, original, original.ProcessedFrames, Muted(), [Muted()[0]]));
        Assert.Equal(original.Segments,
            new AudioRestorePlan(current, original, original.ProcessedFrames, Muted(), Muted()).CreateTranscript().Segments);
    }

    [Fact]
    public void SelectionMustMatchCurrentlyMutedSections()
    {
        var original = Original();
        var current = AudioEditPlan.ForMutedRanges(original, original.ProcessedFrames, Muted()).CreateTranscript();
        Assert.Throws<InvalidOperationException>(() => new AudioRestorePlan(current, original, original.ProcessedFrames, Muted(), []));
        Assert.Throws<InvalidOperationException>(() =>
            new AudioRestorePlan(current, original, original.ProcessedFrames, Muted(), [new(0, PcmFormat.SampleRate)]));
    }

    [Fact]
    public void NormalizationAndSubtractionMatchFrameByFrameCoverage()
    {
        var random = new Random(901);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var original = MakeRanges();
            var remove = MakeRanges();
            var result = AudioRangeSet.Subtract(original, remove, 100);
            for (var frame = 0; frame < 100; frame++)
            {
                var expected = original.Any(range => frame >= range.StartFrame && frame < range.EndFrame) &&
                    !remove.Any(range => frame >= range.StartFrame && frame < range.EndFrame);
                Assert.Equal(expected, AudioRangeSet.Overlaps(result, frame, frame + 1));
            }
            Assert.All(result, range => Assert.True(range.StartFrame < range.EndFrame));
        }

        AudioFrameRange[] MakeRanges() => Enumerable.Range(0, 7).Select(_ =>
        {
            var first = random.Next(0, 100);
            return new AudioFrameRange(first, random.Next(first + 1, 101));
        }).ToArray();
    }

    private static AudioFrameRange[] Muted() =>
        [new(PcmFormat.SampleRate, 2L * PcmFormat.SampleRate), new(3L * PcmFormat.SampleRate, 4L * PcmFormat.SampleRate)];

    private static TranscriptDocument Original()
    {
        var document = AudioEditPlanTests.Document(4,
            new(0, 1, "Kept first.") { SpeakerId = "spkr1" },
            new(1, 2, "Recover first.") { SpeakerId = "spkr2" },
            new(2, 3, "Kept second.") { SpeakerId = "spkr1" },
            new(3, 4, "Recover second.") { SpeakerId = "spkr2" });
        document.SpeakerNames = new() { ["spkr1"] = "Alex", ["spkr2"] = "Original voice" };
        return document;
    }
}
