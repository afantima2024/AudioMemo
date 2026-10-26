using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public sealed record RestoreRangeChoice(AudioFrameRange Range, string Text)
{
    public string Display => Range.Display;
}

public partial class EditSessionWindow : Window
{
    private readonly RecordingSession source;
    private readonly AudioEditPlan? plan;
    private readonly SilenceRecoveryContext? recovery;
    private AudioRestorePlan? restorePlan;
    private readonly string library;
    private bool reuseCurrent;
    private bool choiceMade;
    private readonly RecordingPlayback preview;
    private readonly DispatcherTimer previewClock = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool updatingPreview;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool closeAfterCancellation;
    public RecordingSession? ResultSession { get; private set; }

    public EditSessionWindow(RecordingSession source, AudioEditPlan plan, string library)
        : this(source, library, false)
    {
        this.plan = plan;
        StatisticsText.Text = UiText.Format("EditStatistics", plan.SelectedRowCount, plan.MutedRanges.Count,
            AudioEditPlan.FormatTime(plan.SourceFrames), AudioEditPlan.FormatTime(plan.MutedFrames),
            AudioEditPlan.FormatTime(plan.EditedFrames));
        RangesList.ItemsSource = plan.MutedRanges.Select(range => range.Display);
        EmptyTranscriptWarning.Visibility = plan.RemainingSegmentCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetBusyControls();
    }

    public EditSessionWindow(SilenceRecoveryContext recovery, string library, RecordingPlayback? preview = null)
        : this(recovery.Current, library, true, preview)
    {
        this.recovery = recovery;
        Title = HeadingText.Text = UiText.Get("RestoreMutedSections");
        SourceText.Text = UiText.Format("RestoreOriginalLabel", recovery.Original.Title);
        RangesLabelText.Text = UiText.Get("RestoreRangesLabel");
        SelectionHintText.Text = UiText.Get("RestoreSelectionHint");
        SummaryPolicyText.Text = UiText.Get("AudioEditUpdatePolicy");
        RestoreSelectionButtons.Visibility = Visibility.Visible;
        CreateButton.Content = UiText.Get("RestoreSelectedSections");
        RangesList.SelectionMode = SelectionMode.Multiple;
        RangesList.ItemTemplate = (DataTemplate)FindResource("RestoreRangeTemplate");
        AutomationProperties.SetName(RangesList, UiText.Get("RestoreRangesLabel"));
        RangesList.ItemsSource = recovery.MutedRows().Select(range => new RestoreRangeChoice(range,
            recovery.DescribeRange(range) is { Length: > 0 } text ? text : UiText.Get("MutedRowWithoutText"))).ToArray();
        UpdateRestoreSelection();
    }

    private EditSessionWindow(RecordingSession source, string library, bool restoring, RecordingPlayback? playback = null)
    {
        this.source = source;
        this.library = library;
        reuseCurrent = choiceMade = restoring;
        preview = playback ?? new RecordingPlayback();
        InitializeComponent();
        MessageJournal.SetReason(StatusText, UiText.Get(restoring ? "RestoreMutedSections" : "MuteSelectedRows"));
        Language = XmlLanguage.GetLanguage(UiText.Current.Culture.Name);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        var title = source.Title[..Math.Min(source.Title.Length, 140)];
        if (title.Length > 0 && char.IsHighSurrogate(title[^1]))
            title = title[..^1];
        TitleInput.Text = UiText.Format(restoring ? "RestoredTitle" : "EditedTitle", title);
        FormatBox.SelectedIndex = source.RequestedFormat == "MP3" ? 1 : 0;
        SourceText.Text = UiText.Format("EditSourceLabel", source.Title);
        SaveChoicePanel.Visibility = restoring ? Visibility.Collapsed : Visibility.Visible;
        NewCopyFields.Visibility = Visibility.Collapsed;
        if (!restoring)
        {
            Title = HeadingText.Text = UiText.Get("MuteSelectedRows");
            OriginalPolicyText.Visibility = Visibility.Collapsed;
            CreateButton.IsEnabled = false;
        }
        if (reuseCurrent)
        {
            TitleInput.Text = source.Title;
            NewCopyFields.Visibility = Visibility.Collapsed;
            OriginalPolicyText.Text = UiText.Get("AudioEditOriginalPreserved");
            SummaryPolicyText.Text = UiText.Get("AudioEditUpdatePolicy");
            Title = HeadingText.Text = UiText.Get(restoring ? "RestoreMutedSections" : "ApplyMuteChanges");
            CreateButton.Content = UiText.Get(restoring ? "RestoreSelectedSections" : "ApplyMuteChanges");
        }
        Loaded += (_, _) => { if (restoring) RangesList.Focus(); else EditInPlaceChoice.Focus(); };
        previewClock.Tick += (_, _) => UpdatePreview();
        preview.Completed += error => Dispatcher.BeginInvoke(() =>
        {
            UpdatePreview();
            if (error is not null)
                ShowFailure(UiText.Format("ErrorDetails", error.Message));
        });
        Closed += (_, _) =>
        {
            previewClock.Stop();
            try { preview.Dispose(); }
            catch (Exception ex) { MessageJournal.Record(Owner, ex.Message, UiText.Get("PlayMutedRow"), MessageSeverity.Error); }
        };
    }

    private void SaveMode_Changed(object sender, RoutedEventArgs e)
    {
        choiceMade = true;
        reuseCurrent = EditInPlaceChoice.IsChecked == true;
        NewCopyFields.Visibility = reuseCurrent ? Visibility.Collapsed : Visibility.Visible;
        OriginalPolicyText.Visibility = Visibility.Visible;
        OriginalPolicyText.Text = UiText.Get(reuseCurrent ? "MuteInPlacePolicy" : "EditOriginalPreserved");
        SummaryPolicyText.Text = UiText.Get(reuseCurrent ? "AudioEditUpdatePolicy" : "EditedSummaryPolicy");
        CreateButton.Content = UiText.Get(reuseCurrent ? "ApplyMuteChanges" : "CreateEditedCopy");
        SetBusyControls();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !choiceMade)
            return;
        if (recovery is not null && restorePlan is null)
        {
            ShowFailure(UiText.Get("SelectRestoreRanges"));
            return;
        }
        var title = reuseCurrent ? source.Title : TitleInput.Text.Trim();
        if (title.Length is < 1 or > 160 || title.Any(char.IsControl))
        {
            ShowFailure(UiText.Get("InvalidSessionTitle"));
            return;
        }
        var cancel = new CancellationTokenSource();
        cancellation = cancel;
        busy = true;
        SetBusyControls();
        try
        {
            preview.Stop();
            UpdatePreview();
            var progress = new Progress<SessionEditProgress>(update =>
            {
                if (!busy || !ReferenceEquals(cancellation, cancel) || cancel.IsCancellationRequested)
                    return;
                EditProgress.IsIndeterminate = update.Percent is null;
                EditProgress.Value = update.Percent ?? 0;
                StatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                StatusText.Text = UiText.Get(update.Stage switch
                {
                    SessionEditStage.CopyingAudio => recovery is null ? "EditingCopyingAudio" : "RestoringAudio",
                    SessionEditStage.EncodingMp3 => "EditingEncodingMp3",
                    SessionEditStage.EncodingWave => "ExportingConfiguredAudio",
                    SessionEditStage.SavingCopy => reuseCurrent ? "AudioEditSavingChanges" : "EditingSavingCopy",
                    _ => throw new ArgumentOutOfRangeException(nameof(update))
                });
            });
            var format = FormatBox.SelectedIndex == 1 ? "MP3" : "WAV";
            ResultSession = recovery is not null && restorePlan is not null
                ? await new SilenceRecoveryService().SaveAsync(library, recovery, restorePlan, progress, cancel.Token)
                : await new EditedSessionService().SaveAsync(library, source,
                    plan ?? throw new InvalidOperationException(UiText.Get("SelectEditRows")), title, format, progress, cancel.Token,
                    createCopy: !reuseCurrent);
        }
        catch (OperationCanceledException) { StatusText.Text = UiText.Get("EditingCancelled"); }
        catch (Exception ex) { ShowFailure(UiText.Format("ErrorDetails", ex.Message)); }
        finally
        {
            busy = false;
            EditProgress.IsIndeterminate = false;
            cancel.Dispose();
            cancellation = null;
            SetBusyControls();
        }
        if (ResultSession is not null)
            DialogResult = true;
        else if (closeAfterCancellation)
            Close();
    }

    private void SetBusyControls()
    {
        TitleInput.IsEnabled = !busy;
        FormatBox.IsEnabled = !busy;
        CreateButton.IsEnabled = !busy && choiceMade && (recovery is null || restorePlan is not null);
        RangesList.IsEnabled = !busy;
        SaveChoicePanel.IsEnabled = !busy;
        RestoreSelectionButtons.IsEnabled = !busy;
        CancelButton.Content = UiText.Get(busy ? "CancelEditing" : "Cancel");
    }

    private void PlayMutedRow_Click(object sender, RoutedEventArgs e)
    {
        if (busy || recovery is null || (sender as FrameworkElement)?.DataContext is not RestoreRangeChoice choice)
            return;
        try
        {
            preview.PlayRecovery(recovery, choice.Range);
            previewClock.Start();
            UpdatePreview();
        }
        catch (Exception ex) { UpdatePreview(); ShowFailure(UiText.Format("ErrorDetails", ex.Message)); }
    }

    private void UpdatePreview()
    {
        var snapshot = preview.Snapshot;
        MutedReplayTransport.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        if (snapshot is null)
        {
            previewClock.Stop();
            return;
        }
        MutedReplayPause.Content = UiText.Get(snapshot.IsPaused ? "ReplayResume" : "ReplayPause");
        MutedReplayPosition.Text = UiText.Format("ReplayPosition", TranscriptDocument.Timestamp(snapshot.PositionSeconds),
            TranscriptDocument.Timestamp(snapshot.EndSeconds));
        updatingPreview = true;
        try
        {
            MutedReplayProgress.Minimum = snapshot.StartSeconds;
            MutedReplayProgress.Maximum = snapshot.EndSeconds;
            MutedReplayProgress.Value = snapshot.PositionSeconds;
        }
        finally { updatingPreview = false; }
    }

    private void MutedReplayPause_Click(object sender, RoutedEventArgs e)
    {
        try { preview.TogglePause(); UpdatePreview(); }
        catch (Exception ex) { ShowFailure(UiText.Format("ErrorDetails", ex.Message)); }
    }

    private void MutedReplayStop_Click(object sender, RoutedEventArgs e)
    {
        try { preview.Stop(); UpdatePreview(); }
        catch (Exception ex) { ShowFailure(UiText.Format("ErrorDetails", ex.Message)); }
    }

    private void MutedReplaySeek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingPreview || preview.Snapshot is null)
            return;
        try { preview.Seek(e.NewValue); UpdatePreview(); }
        catch (Exception ex) { ShowFailure(UiText.Format("ErrorDetails", ex.Message)); }
    }

    private void ShowFailure(string message)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
    }

    private void SelectAllRestore_Click(object sender, RoutedEventArgs e) => RangesList.SelectAll();
    private void ClearRestoreSelection_Click(object sender, RoutedEventArgs e) => RangesList.UnselectAll();

    private void RestoreSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRestoreSelection();

    private void UpdateRestoreSelection()
    {
        if (recovery is null || busy)
            return;
        restorePlan = null;
        RebuildTranscriptWarning.Visibility = Visibility.Collapsed;
        try
        {
            var selected = RangesList.SelectedItems.Cast<RestoreRangeChoice>().Select(choice => choice.Range).ToArray();
            StatisticsText.Text = UiText.Format("RestoreStatistics", selected.Length,
                AudioEditPlan.FormatTime(selected.Sum(range => range.Length)), AudioEditPlan.FormatTime(source.Frames));
            if (selected.Length == 0)
                StatusText.Text = UiText.Get("SelectRestoreRanges");
            else
            {
                restorePlan = recovery.Plan(selected);
                StatusText.Text = "";
                RebuildTranscriptWarning.Visibility = restorePlan.RebuildsTranscript ? Visibility.Visible : Visibility.Collapsed;
                EmptyTranscriptWarning.Visibility = restorePlan.CreateTranscript().Segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex) { ShowFailure(UiText.Format("ErrorDetails", ex.Message)); }
        SetBusyControls();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!busy)
            Close();
        else
        {
            closeAfterCancellation = true;
            cancellation?.Cancel();
            StatusText.Text = UiText.Get("EditingCancelling");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!busy)
            return;
        e.Cancel = true;
        closeAfterCancellation = true;
        cancellation?.Cancel();
        StatusText.Text = UiText.Get("EditingCancelling");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        Cancel_Click(sender, e);
        e.Handled = true;
    }
}
