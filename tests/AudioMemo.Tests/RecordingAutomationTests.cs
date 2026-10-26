using System.Text.Json;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Tests;

public sealed class RecordingAutomationTests
{
    [Fact]
    public void ExistingSettingsKeepManualStopAndTheFiveMinuteDefault()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Format":"MP3","IdentifySpeakersOnStop":false}""")!;
        settings.Validate();
        Assert.False(settings.AutoStopOnSilence);
        Assert.Equal(300, settings.SilenceTimeoutSeconds);
        Assert.False(settings.IdentifySpeakersOnStop);
        Assert.Equal("MP3", settings.Format);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(86400)]
    public void RecordingAutomationSettingsRoundTrip(int seconds)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { AutoStopOnSilence = true, SilenceTimeoutSeconds = seconds, IdentifySpeakersOnStop = false };
        var path = folder.File("settings.json");
        AtomicFile.WriteJson(path, settings);
        var saved = JsonSerializer.Deserialize<AppSettings>(AtomicFile.ReadText(path))!;
        saved.Validate();
        Assert.True(saved.AutoStopOnSilence);
        Assert.Equal(seconds, saved.SilenceTimeoutSeconds);
        Assert.False(saved.IdentifySpeakersOnStop);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(86401)]
    [InlineData(int.MaxValue)]
    public void InvalidSilenceTimeoutIsRejected(int seconds) =>
        Assert.Throws<InvalidDataException>(() => new AppSettings { SilenceTimeoutSeconds = seconds }.Validate());

    [Fact]
    public void FiveMinutesMeansFiveMinutesOfAudioFramesEvenWithoutAnyPackets()
    {
        var frames = 300L * PcmFormat.SampleRate;
        var detector = new RecordingSilenceDetector(frames);
        Assert.False(detector.HasTimedOut(frames - 1));
        Assert.True(detector.HasTimedOut(frames));
        Assert.True(detector.HasTimedOut(frames + 1));
    }

    [Fact]
    public void ActivityOnEitherChannelOrSourceRestartsTheFullTimeout()
    {
        var detector = new RecordingSilenceDetector(10);
        detector.Observe(5, [0, -0.1f, 0, 0], 1);
        Assert.False(detector.HasTimedOut(15));
        Assert.True(detector.HasTimedOut(16));
        detector.Observe(20, [0.2f, 0, 0, 0], 1);
        detector.Observe(20, [-0.2f, 0, 0, 0], 1);
        detector.Observe(0, [0.4f, 0.4f], 1);
        Assert.False(detector.HasTimedOut(30));
        Assert.True(detector.HasTimedOut(31));
    }

    [Fact]
    public void SilenceUsesTheSelectedGainAndTheExactAmplitudeBoundary()
    {
        var detector = new RecordingSilenceDetector(10);
        detector.Observe(1, [0.5f, 0.5f], 0);
        detector.Observe(2, [0.0009f, -0.0009f], 1);
        Assert.True(detector.HasTimedOut(10));
        detector.Observe(9, [0, -RecordingSilenceDetector.AudibleAmplitude], 1);
        Assert.False(detector.HasTimedOut(19));
        Assert.True(detector.HasTimedOut(20));
        detector.Observe(19, [RecordingSilenceDetector.AudibleAmplitude / 2, 0], 2);
        Assert.False(detector.HasTimedOut(29));
        Assert.True(detector.HasTimedOut(30));
    }

    [Fact]
    public void ResumingOrChangingSourcesStartsANewContinuousSilenceInterval()
    {
        var detector = new RecordingSilenceDetector(10);
        Assert.True(detector.HasTimedOut(10));
        detector.Reset(10);
        Assert.False(detector.HasTimedOut(10));
        Assert.False(detector.HasTimedOut(19));
        Assert.True(detector.HasTimedOut(20));
    }

    [Fact]
    public void InvalidDetectionInputIsNotSilentlyAccepted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingSilenceDetector(0));
        var detector = new RecordingSilenceDetector(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => detector.Reset(-1));
        Assert.Throws<ArgumentException>(() => detector.Observe(0, [1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => detector.Observe(0, [0, 0], float.NaN));
    }
}
