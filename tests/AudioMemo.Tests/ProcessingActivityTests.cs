using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class ProcessingActivityTests
{
    [Fact]
    public void CompletionWaitsForBothAudioSavingAndLiveAi()
    {
        var activity = new ProcessingActivity();
        var liveAi = activity.Begin();
        var stop = activity.Begin();
        var export = activity.Begin();
        activity.Finish(liveAi, ProcessingOutcome.Completed);
        activity.Acknowledge();
        Assert.Equal(ProcessingOutcome.Working, activity.Outcome);
        activity.Finish(export, ProcessingOutcome.Completed);
        Assert.Equal(ProcessingOutcome.Working, activity.Outcome);
        activity.Finish(stop, ProcessingOutcome.Completed);
        Assert.Equal(ProcessingOutcome.Completed, activity.Outcome);
        activity.Acknowledge();
        Assert.Equal(ProcessingOutcome.None, activity.Outcome);
    }

    [Fact]
    public void UnsuccessfulWorkCannotTurnGreenWhenOtherWorkFinishes()
    {
        foreach (var outcome in new[] { ProcessingOutcome.Warning, ProcessingOutcome.Cancelled, ProcessingOutcome.Failed })
        {
            var activity = new ProcessingActivity();
            var first = activity.Begin();
            var second = activity.Begin();
            activity.Finish(first, outcome);
            activity.Finish(second, ProcessingOutcome.Completed);
            Assert.Equal(outcome, activity.Outcome);
            var retry = activity.Begin();
            activity.Finish(retry, ProcessingOutcome.Completed);
            Assert.Equal(ProcessingOutcome.Completed, activity.Outcome);
        }
    }

    [Fact]
    public void WarningsBelongToTheirOperationAndDoNotLeakIntoLaterWork()
    {
        var activity = new ProcessingActivity();
        var first = activity.Begin();
        activity.Warn(first);
        activity.Finish(first, ProcessingOutcome.Completed);
        Assert.Equal(ProcessingOutcome.Warning, activity.Outcome);
        var next = activity.Begin();
        activity.Warn(first);
        activity.Finish(next, ProcessingOutcome.Completed);
        Assert.Equal(ProcessingOutcome.Completed, activity.Outcome);
    }
}
