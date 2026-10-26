using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using AudioMemo.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    private readonly ProcessingActivity processing = new();
    private readonly ProcessingTiming processingTiming;
    private readonly Stopwatch recordingPulse = new();
    private readonly DispatcherTimer recordingPulseTimer = new(DispatcherPriority.Background)
    {
        Interval = AppIcons.RecordingFrameInterval
    };
    private long aiActivityId;
    private long aiJobVersion;
    private (string SessionId, string OperationKey)? activeAiOperation;

    private void InitializeActivityFeedback()
    {
        TaskbarItemInfo = new();
        recordingPulseTimer.Tick += (_, _) => UpdateTaskbarIcon();
        Closed += (_, _) =>
        {
            recordingPulseTimer.Stop();
            recordingPulse.Reset();
            processingTiming.Stop();
        };
        Activated += (_, _) => AcknowledgeProcessing();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, _) => AcknowledgeProcessing()), true);
        AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => AcknowledgeProcessing()), true);
        UpdateActivityDisplay();
    }

    private long BeginProcessing()
    {
        var id = processing.Begin();
        UpdateActivityDisplay();
        return id;
    }

    private void FinishProcessing(long id, ProcessingOutcome outcome)
    {
        processing.Finish(id, outcome);
        if (id == aiActivityId)
            processingTiming.ClearEstimate();
        UpdateActivityDisplay();
    }

    private void AcknowledgeProcessing()
    {
        processing.Acknowledge();
        UpdateActivityDisplay();
    }

    private void UpdateTaskbarIcon()
    {
        var state = recorder.State;
        ImageSource icon;
        if (state == RecorderState.Recording && !closing && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
        {
            if (!recordingPulse.IsRunning)
            {
                recordingPulse.Restart();
                recordingPulseTimer.Start();
            }
            icon = AppIcons.RecordingAt(recordingPulse.Elapsed, settings.Appearance.RainbowRecordingIndicator);
        }
        else
        {
            recordingPulseTimer.Stop();
            recordingPulse.Reset();
            icon = state is RecorderState.Recording or RecorderState.Stopping ? AppIcons.Recording :
                state == RecorderState.Paused ? AppIcons.Paused : AppIcons.Logo;
        }
        if (!ReferenceEquals(Icon, icon))
            Icon = icon;
    }

    private void UpdateActivityDisplay()
    {
        var state = recorder.State;
        UpdateTaskbarIcon();
        // The full icon allows a larger recording badge than Windows' small overlay slot.
        TaskbarItemInfo.Overlay = null;
        var outcome = RecordingActive ? ProcessingOutcome.None : processing.Outcome;
        var key = outcome switch
        {
            ProcessingOutcome.Working => "ProcessingWorking",
            ProcessingOutcome.Completed => "ProcessingComplete",
            ProcessingOutcome.Warning => "ProcessingWarning",
            ProcessingOutcome.Cancelled => "ProcessingCancelled",
            ProcessingOutcome.Failed => "ProcessingFailed",
            _ => null
        };
        ProcessingIndicator.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
        ProcessingLabel.Text = key is null ? "" : UiText.Get(key);
        ProcessingLabel.ToolTip = ProcessingLabel.Text + (outcome == ProcessingOutcome.Working
            ? Environment.NewLine + UiText.Get("ProcessingStagesHint") : "");
        ProcessingProgress.IsIndeterminate = outcome == ProcessingOutcome.Working;
        ProcessingProgress.Value = outcome == ProcessingOutcome.None ? 0 : 1;
        if (outcome == ProcessingOutcome.Completed && !SystemParameters.HighContrast)
            ProcessingProgress.Foreground = Brushes.ForestGreen;
        else
            ProcessingProgress.SetResourceReference(Control.ForegroundProperty, outcome switch
            {
                ProcessingOutcome.Failed => "SystemFillColorCriticalBrush",
                ProcessingOutcome.Warning or ProcessingOutcome.Cancelled => "SystemFillColorCautionBrush",
                _ => "AccentTextFillColorPrimaryBrush"
            });
        TaskbarItemInfo.ProgressState = outcome switch
        {
            ProcessingOutcome.Working => TaskbarItemProgressState.Indeterminate,
            ProcessingOutcome.Completed => TaskbarItemProgressState.Normal,
            ProcessingOutcome.Warning or ProcessingOutcome.Cancelled => TaskbarItemProgressState.Paused,
            ProcessingOutcome.Failed => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.None
        };
        TaskbarItemInfo.ProgressValue = outcome == ProcessingOutcome.None ? 0 : 1;
        TaskbarItemInfo.Description = "AudioMemo" + (key is not null ? " - " + UiText.Get(key) :
            state == RecorderState.Paused ? " - " + UiText.Get("StatePaused") :
            state is RecorderState.Recording or RecorderState.Stopping ? " - " + UiText.Get("StateRecording") : "");
        UpdateProcessingTiming(outcome);
    }

    private void UpdateProcessingTiming(ProcessingOutcome outcome)
    {
        if (outcome == ProcessingOutcome.None)
            processingTiming.Reset();
        else if (outcome == ProcessingOutcome.Working)
        {
            if (!processingTiming.IsRunning)
                processingTiming.Start();
        }
        else
            processingTiming.Stop();
        var display = processingTiming.Display(UiText.Current);
        ProcessingTimingText.Text = display.Text;
        ProcessingTimingText.ToolTip = display.ToolTip;
        ProcessingTimingText.Visibility = processingTiming.HasStarted ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task StopIfSilentAsync()
    {
        if (recordingCommand || closing || !recorder.SilenceTimeoutReached)
            return;
        await RunRecordingCommandAsync(async () =>
        {
            if (!recorder.SilenceTimeoutReached)
                return;
            var session = recorder.Session;
            var priorWarning = warningVersion;
            await StopRecordingAsync();
            if (session?.Status == "Saved")
                SetStatus(UiText.Format("RecordingStoppedOnSilence", settings.SilenceTimeoutSeconds), warningVersion == priorWarning);
        });
    }
}
