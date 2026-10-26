using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

internal enum TranscriptReviewChoice { Cancel, Apply, Keep, Recheck, Resume }

public partial class TranscriptReviewWindow : Window
{
    private readonly RecordingSession session;
    private readonly TranscriptDocument source;
    private readonly RecordingPlayback replay;
    private readonly bool canResume;
    internal IReadOnlyList<TranscriptReviewRow> Rows { get; }
    internal TranscriptReviewChoice Choice { get; private set; }
    internal bool IsClosed { get; private set; }
    internal IReadOnlyDictionary<int, string> Corrections =>
        Rows.Where(row => row.ApplyCorrection).ToDictionary(row => row.SegmentIndex, row => row.Text);

    internal TranscriptReviewWindow(RecordingSession session, TranscriptReviewAnalysis analysis, AppSettings settings,
        RecordingPlayback? playback = null)
    {
        this.session = session;
        source = analysis.Source;
        canResume = analysis.CanResume;
        replay = playback ?? new();
        TranscriptReview.Validate(analysis.Report, source);
        Rows = analysis.Report.Items.GroupBy(item => item.SegmentIndex).OrderBy(group => group.Key)
            .Select(group => new TranscriptReviewRow(group.Key, source.Segments[group.Key],
                source.Segments[group.Key].SpeakerId is { } id ? source.SpeakerName(id) : UiText.Get("UnlabeledSpeakers"),
                string.Join(Environment.NewLine, group.Select(item => $"{item.Quote}: {item.Reason}")))).ToArray();
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(settings.UiLanguage);
        Icon = AppIcons.Logo;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        ReviewRows.ItemsSource = Rows;
        if (!analysis.Report.AnalysisComplete)
            ReviewAgainButton.Content = UiText.Get(canResume ? "TranscriptReviewResume" : "TranscriptReviewRetry");
        if (canResume)
            ReviewAgainButton.ToolTip = UiText.Get("TranscriptReviewResumeHint");
        ReviewCaption.Text = analysis.Report.Items.Count == 0
            ? UiText.Get(analysis.Report.AnalysisComplete ? "TranscriptReviewNoItems" : "TranscriptReviewNoVerifiedResult")
            : UiText.Format("TranscriptReviewCount", analysis.Report.Items.Count, Rows.Count);
        ReviewModel.Text = UiText.Format("TranscriptReviewModel",
            SummaryProviders.Label(analysis.Report.Provider, UiText.Current),
            string.IsNullOrWhiteSpace(analysis.Report.Model) ? UiText.Get("CliDefaultModel") : analysis.Report.Model,
            analysis.Report.CheckedAt.ToLocalTime().ToString("g", UiText.Current.Culture));
        if (analysis.Report.Error is not null)
        {
            ReviewWarningPanel.Visibility = Visibility.Visible;
            ReviewWarning.Text = analysis.Report.Error;
        }
        ReviewStatus.Text = UiText.Get("TranscriptReviewSelectionHint");
        replay.Completed += error => Dispatcher.BeginInvoke(() =>
        {
            if (IsClosed)
                return;
            ReviewStatus.Text = error is null ? UiText.Get("ReplayStopped") : UiText.Format("ErrorDetails", error.Message);
            UpdatePlayback();
        });
        Closed += (_, _) =>
        {
            IsClosed = true;
            try { replay.Dispose(); }
            catch (Exception ex)
            {
                MessageJournal.Record(this, UiText.Format("ErrorDetails", ex.Message),
                    UiText.Get("ReviewUnclearTranscript"), MessageSeverity.Error);
            }
        };
        ReviewRows.SelectedIndex = Rows.Count > 0 ? 0 : -1;
        UpdatePlayback();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitCell();
            var changes = Corrections;
            if (changes.Count > 0)
            {
                var indices = changes.Keys.Order().ToArray();
                TranscriptCorrectionPlan.CreateForSegments(source, session.Frames, indices)
                    .Apply(indices.Select(index => changes[index]).ToArray());
            }
            Choice = TranscriptReviewChoice.Apply;
            Close();
        }
        catch (Exception ex) { ReviewStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void Keep_Click(object sender, RoutedEventArgs e) { Choice = TranscriptReviewChoice.Keep; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Recheck_Click(object sender, RoutedEventArgs e)
    {
        CommitCell();
        if (Rows.Any(row => row.Text != row.OriginalText) &&
            MessageDialog.ShowMessage(this, UiText.Get("TranscriptReviewDiscardForRecheck"), UiText.Get("ReviewUnclearTranscript"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Choice = canResume ? TranscriptReviewChoice.Resume : TranscriptReviewChoice.Recheck;
        Close();
    }

    private void CommitCell()
    {
        if (!ReviewRows.CommitEdit(DataGridEditingUnit.Cell, true) || !ReviewRows.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException(UiText.Get("InvalidCorrectionText"));
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdatePlayback();
    private void UpdatePlayback()
    {
        if (ReviewPlayButton is null)
            return;
        ReviewPlayButton.IsEnabled = ReviewRows.SelectedItem is TranscriptReviewRow row &&
            source.Segments[row.SegmentIndex].EndSeconds > source.Segments[row.SegmentIndex].StartSeconds &&
            File.Exists(session.WavePath);
        ReviewPauseButton.IsEnabled = ReviewStopButton.IsEnabled = replay.Snapshot is not null;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ReviewRows.SelectedItem is not TranscriptReviewRow row)
                return;
            var segment = source.Segments[row.SegmentIndex];
            replay.Play(session, new PlaybackRange(Math.Max(0, segment.StartSeconds - 1),
                Math.Min((double)session.Frames / PcmFormat.SampleRate, segment.EndSeconds + 1)));
            ReviewStatus.Text = UiText.Get("TranscriptReviewListening");
            UpdatePlayback();
        }
        catch (Exception ex) { ReviewStatus.Text = UiText.Format("ErrorDetails", ex.Message); UpdatePlayback(); }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        try { replay.TogglePause(); UpdatePlayback(); }
        catch (Exception ex) { ReviewStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { replay.Stop(); ReviewStatus.Text = UiText.Get("ReplayStopped"); UpdatePlayback(); }
        catch (Exception ex) { ReviewStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }
}

internal sealed class TranscriptReviewRow(int index, TranscriptSegment segment, string speaker, string reason) : INotifyPropertyChanged
{
    private string text = segment.Text;
    private bool apply;
    public int SegmentIndex => index;
    public string OriginalText => segment.Text;
    public string Time => $"{TranscriptDocument.Timestamp(segment.StartSeconds)} - {TranscriptDocument.Timestamp(segment.EndSeconds)}";
    public string Speaker => speaker;
    public string Reason => reason;
    public string Text
    {
        get => text;
        set
        {
            if (text == value)
                return;
            text = value;
            ApplyCorrection = text != OriginalText;
            PropertyChanged?.Invoke(this, new(nameof(Text)));
        }
    }
    public bool ApplyCorrection
    {
        get => apply;
        set
        {
            if (apply == value)
                return;
            apply = value;
            PropertyChanged?.Invoke(this, new(nameof(ApplyCorrection)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
