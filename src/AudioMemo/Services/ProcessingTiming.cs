using System.Globalization;
using AudioMemo.Core.Localization;

namespace AudioMemo.Services;

internal sealed class ProcessingTiming(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private long started;
    private TimeSpan accumulated;
    private string? phase;
    private double? firstFraction;
    private double? fraction;
    private long phaseStarted;
    private long lastAdvanced;
    private TimeSpan lastInterval;

    public bool HasStarted { get; private set; }
    public bool IsRunning { get; private set; }
    public TimeSpan Elapsed => accumulated + (IsRunning ? time.GetElapsedTime(started) : TimeSpan.Zero);

    public void Start(bool resume = false)
    {
        if (IsRunning)
            throw new InvalidOperationException("Processing timing is already running.");
        if (!resume)
            accumulated = TimeSpan.Zero;
        started = time.GetTimestamp();
        HasStarted = IsRunning = true;
        ClearEstimate();
    }

    public void Stop()
    {
        if (IsRunning)
            accumulated = Elapsed;
        IsRunning = false;
        ClearEstimate();
    }

    public void Reset()
    {
        Stop();
        HasStarted = false;
        accumulated = TimeSpan.Zero;
    }

    public void ClearEstimate()
    {
        phase = null;
        firstFraction = fraction = null;
        lastInterval = TimeSpan.Zero;
    }

    public void Report(string nextPhase, double? nextFraction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nextPhase);
        if (nextFraction is { } value && (!double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(nextFraction));
        if (!IsRunning)
            throw new InvalidOperationException("Processing timing is not running.");
        var now = time.GetTimestamp();
        if (phase != nextPhase || fraction.HasValue != nextFraction.HasValue || nextFraction < fraction)
        {
            phase = nextPhase;
            firstFraction = fraction = nextFraction;
            phaseStarted = lastAdvanced = now;
            lastInterval = TimeSpan.Zero;
        }
        else if (nextFraction > fraction)
        {
            lastInterval = time.GetElapsedTime(lastAdvanced, now);
            lastAdvanced = now;
            fraction = nextFraction;
        }
    }

    public TimeSpan? Remaining
    {
        get
        {
            if (!IsRunning || firstFraction is not { } baseline || fraction is not { } current ||
                current <= baseline || current >= 1)
                return null;
            var sampled = time.GetElapsedTime(phaseStarted, lastAdvanced);
            if (sampled < TimeSpan.FromSeconds(3))
                return null;
            var sinceProgress = time.GetElapsedTime(lastAdvanced);
            if (sinceProgress.TotalSeconds > Math.Max(30, 3 * lastInterval.TotalSeconds))
                return null;
            var seconds = sampled.TotalSeconds * (1 - current) / (current - baseline) - sinceProgress.TotalSeconds;
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds >= TimeSpan.MaxValue.TotalSeconds - 1)
                return null;
            return TimeSpan.FromSeconds(Math.Ceiling(seconds));
        }
    }

    public (string Text, string ToolTip) Display(UiText text)
    {
        if (!HasStarted)
            return ("", "");
        var elapsed = Duration(Elapsed);
        var caption = text.FormatValue("ProcessingElapsed", elapsed);
        var tooltip = text["ProcessingTimingHint"];
        if (!IsRunning)
            return (caption, caption + Environment.NewLine + tooltip);
        if (phase == "transcript-review-confirm")
        {
            caption = text.FormatValue("ProcessingWaitingForTranscriptReview", elapsed);
            return (caption, caption);
        }
        var now = time.GetLocalNow();
        if (Remaining is { } remaining && remaining < DateTimeOffset.MaxValue - now &&
            remaining < DateTime.MaxValue - now.DateTime)
        {
            var finish = now + remaining;
            var formatted = finish.ToString(finish.Date == now.Date ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", text.Culture);
            caption = text.FormatValue("ProcessingStepEstimate", elapsed, formatted);
            tooltip += Environment.NewLine + text.FormatValue("ProcessingStepRemaining", Duration(remaining));
        }
        else
            caption = text.FormatValue("ProcessingEstimating", elapsed);
        return (caption, caption + Environment.NewLine + tooltip);
    }

    private static string Duration(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
}
