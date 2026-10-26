using System.Windows;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    internal MessageHistoryStore MessageHistory => storage.Messages;

    private void MessageHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!closing)
            new MessageHistoryWindow(this, storage.Messages).Show();
    }

    internal void ShowHistoryWriteWarning(string warning)
    {
        WarningText.Text += (WarningText.Text.Length == 0 ? "" : Environment.NewLine) + warning;
        WarningText.ToolTip = WarningText.Text;
        WarningStampText.Text = UiText.Format("WarningRecordedAt", DateTime.Now);
        WarningBanner.Visibility = Visibility.Visible;
    }

    private string MessageReason(string operation)
    {
        if (operation is "TranscribeResume" or "UpdateMainPoints" or "SummarizeAboutMe" or
            "IdentifySpeakers" or "ResumeProcessing" or "RecheckUnlabeledSpeakers" or "LlmLabel")
            return UiText.Get(operation);
        if (activeAiOperation is { } active && operation is "StartAiJobCore" or "ExecuteAiAsync")
            return UiText.Get(active.OperationKey);
        var key = operation switch
        {
            _ when operation.Contains("Speaker", StringComparison.Ordinal) => "IdentifySpeakers",
            _ when operation.Contains("Replay", StringComparison.Ordinal) => "PlaySelectedRange",
            _ when operation.Contains("Model", StringComparison.Ordinal) || operation.Contains("Provider", StringComparison.Ordinal) => "LlmLabel",
            _ when operation.Contains("Setting", StringComparison.Ordinal) || operation.Contains("Device", StringComparison.Ordinal) => "Settings",
            _ when operation.Contains("Recording", StringComparison.Ordinal) => "StartRecording",
            _ when operation.Contains("Note", StringComparison.Ordinal) => "MyNotes",
            _ when operation.Contains("Transcript", StringComparison.Ordinal) => "Transcript",
            _ when operation.Contains("Restore", StringComparison.Ordinal) || operation.Contains("Edit", StringComparison.Ordinal) => "MuteSelectedRows",
            _ when operation.Contains("Export", StringComparison.Ordinal) => "ExportConfiguredAudio",
            _ when operation.Contains("History", StringComparison.Ordinal) || operation.Contains("Session", StringComparison.Ordinal) => "YourRecordings",
            _ => "MessageApplication"
        };
        return UiText.Get(key);
    }
}
