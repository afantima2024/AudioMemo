using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class ProcessingTimingTests
{
    [Fact]
    public void ResumedProgressEstimatesOnlyTheWorkMeasuredInThisRun()
    {
        var time = new ManualTime();
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("transcription", 0.8);
        Assert.Null(timing.Remaining);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("transcription", 0.9);
        Assert.InRange(timing.Remaining!.Value.TotalSeconds, 10, 11);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.InRange(timing.Remaining!.Value.TotalSeconds, 5, 6);
        Assert.Equal(TimeSpan.FromSeconds(15), timing.Elapsed);
        Assert.Contains("Step ETA", timing.Display(new UiText("en")).Text);
    }

    [Fact]
    public void PhasesAndBackwardProgressInvalidateTheOldEstimate()
    {
        var time = new ManualTime();
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("transcription", 0);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("transcription", 0.25);
        Assert.Equal(TimeSpan.FromSeconds(30), timing.Remaining);
        timing.Report("speakers", null);
        Assert.Null(timing.Remaining);
        time.Advance(TimeSpan.FromMinutes(5));
        timing.Report("speaker-features", 0.1);
        Assert.Null(timing.Remaining);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("speaker-features", 0.2);
        Assert.Equal(TimeSpan.FromSeconds(80), timing.Remaining);
        timing.Report("speaker-features", 0.05);
        Assert.Null(timing.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(320), timing.Elapsed);
    }

    [Fact]
    public void ACompletedStepIsNotMistakenForACompletedJob()
    {
        var time = new ManualTime();
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("main-points", 0);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("main-points", 1);
        Assert.True(timing.IsRunning);
        Assert.Null(timing.Remaining);
        timing.Report("overall-summary", null);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromSeconds(70), timing.Elapsed);
        Assert.Contains("Estimating", timing.Display(new UiText("en")).Text);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void WaitingForReviewHasNoEtaAndDoesNotSkewLaterModelEstimates(string language)
    {
        var time = new ManualTime();
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("transcript-review", 0);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("transcript-review", 0.5);
        Assert.NotNull(timing.Remaining);
        timing.Report("transcript-review-confirm", null);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Null(timing.Remaining);
        Assert.Equal(new UiText(language).FormatValue("ProcessingWaitingForTranscriptReview", "01:00:10"),
            timing.Display(new UiText(language)).Text);
        timing.Report("main-points", 0);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("main-points", 0.5);
        Assert.Equal(TimeSpan.FromSeconds(10), timing.Remaining);
    }

    [Theory]
    [InlineData(0.5, 11)]
    [InlineData(0.01, 31)]
    public void ExpiredOrStalledEstimatesAreWithdrawnEvenWithDuplicateUpdates(double fraction, int stalledSeconds)
    {
        var time = new ManualTime();
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("transcription", 0);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("transcription", fraction);
        Assert.NotNull(timing.Remaining);
        time.Advance(TimeSpan.FromSeconds(stalledSeconds));
        timing.Report("transcription", fraction);
        Assert.Null(timing.Remaining);
        Assert.Contains("Estimating", timing.Display(new UiText("en")).Text);
        Assert.Equal(TimeSpan.FromSeconds(10 + stalledSeconds), timing.Elapsed);
    }

    [Fact]
    public void PausingFreezesElapsedTimeAndResumingExcludesIdleTime()
    {
        var time = new ManualTime();
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("selection", 0);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("selection", 0.5);
        timing.Stop();
        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromSeconds(10), timing.Elapsed);
        Assert.Equal("Elapsed 00:00:10", timing.Display(new UiText("en")).Text);
        timing.Start(resume: true);
        timing.Report("selection", 0.5);
        Assert.Null(timing.Remaining);
        time.Advance(TimeSpan.FromSeconds(5));
        timing.Report("selection", 0.75);
        Assert.Equal(TimeSpan.FromSeconds(15), timing.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(5), timing.Remaining);
        timing.Stop();
        timing.Start();
        Assert.Equal(TimeSpan.Zero, timing.Elapsed);
        Assert.Null(timing.Remaining);
        timing.Reset();
        Assert.False(timing.HasStarted);
        Assert.False(timing.IsRunning);
        Assert.Empty(timing.Display(new UiText("en")).Text);
    }

    [Fact]
    public void WallClockChangesDoNotChangeElapsedTimeOrRemainingWork()
    {
        var time = new ManualTime();
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("transcription", 0);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("transcription", 0.25);
        time.UtcNow -= TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromSeconds(10), timing.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(30), timing.Remaining);
        Assert.Contains("08:00:40", timing.Display(new UiText("en")).Text);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void FormattingSupportsLongRunsAndFinishTimesOnAnotherDate(string language)
    {
        var text = new UiText(language);
        var time = new ManualTime { UtcNow = new DateTimeOffset(2026, 10, 23, 23, 59, 40, TimeSpan.Zero) };
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("transcription", 0);
        time.Advance(TimeSpan.FromSeconds(10));
        timing.Report("transcription", 0.25);
        Assert.Contains("2026-10-24 00:00:20", timing.Display(text).Text);
        Assert.Contains(text.FormatValue("ProcessingStepRemaining", "00:00:30"), timing.Display(text).ToolTip);
        timing.Report("unknown", null);
        time.Advance(TimeSpan.FromHours(49));
        Assert.Contains("49:00:10", timing.Display(text).Text);
        timing.Stop();
        Assert.Equal(text.FormatValue("ProcessingElapsed", "49:00:10"), timing.Display(text).Text);
    }

    [Fact]
    public void VeryShortSamplesAndImpossibleFinishDatesDoNotProduceAnEta()
    {
        var time = new ManualTime();
        var timing = new ProcessingTiming(time);
        timing.Start();
        timing.Report("transcription", 0);
        time.Advance(TimeSpan.FromSeconds(1));
        timing.Report("transcription", 0.1);
        Assert.Null(timing.Remaining);
        time.Advance(TimeSpan.FromSeconds(9));
        timing.Report("transcription", 0.2);
        time.UtcNow = DateTimeOffset.MaxValue - TimeSpan.FromSeconds(1);
        Assert.Equal("Elapsed 00:00:10 | Estimating...", timing.Display(new UiText("en")).Text);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void InvalidProgressIsRejected(double fraction)
    {
        var timing = new ProcessingTiming();
        timing.Start();
        Assert.Throws<ArgumentOutOfRangeException>(() => timing.Report("transcription", fraction));
    }

    internal sealed class ManualTime : TimeProvider
    {
        private long timestamp;
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            timestamp = checked(timestamp + duration.Ticks);
            UtcNow += duration;
        }
    }
}
