using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Audio;

namespace AudioMemo.Controls;

public partial class TranscriptView : UserControl
{
    private readonly ObservableCollection<TranscriptRow> rows = [];
    private TranscriptDocument document = new();
    private TranscriptDocument? pending;
    private ScrollViewer? scroll;
    private bool updating;
    private readonly SpeakerFilter speakerFilter = new();
    private IReadOnlyList<SpeakerChoice> speakerChoices = [];
    private bool canReplay;
    private bool canExportAudio;
    private bool canEdit;
    private bool canCorrect;
    private bool canReviewUnclear;
    private bool canChangeSpeakers;
    private bool canSetSpeakerColors;
    private bool canSummarizeAboutMe;
    private bool canRestoreMutedRows;
    private bool canRecheckUnlabeled;
    private PlaybackSnapshot? playback;
    private bool updatingPlayback;
    private bool draggingPlayback;
    private double playbackDragStart;
    public bool CanChangeSpeakers
    {
        get => canChangeSpeakers;
        set => SetCapability(ref canChangeSpeakers, value);
    }
    public bool CanSetSpeakerColors
    {
        get => canSetSpeakerColors;
        set => SetCapability(ref canSetSpeakerColors, value);
    }
    public bool CanSummarizeAboutMe
    {
        get => canSummarizeAboutMe;
        set => SetCapability(ref canSummarizeAboutMe, value);
    }
    public bool CanCorrect
    {
        get => canCorrect;
        set => SetCapability(ref canCorrect, value);
    }
    public bool CanReviewUnclear
    {
        get => canReviewUnclear;
        set => SetCapability(ref canReviewUnclear, value);
    }
    public event Action? ReviewUnclearRequested;
    public bool IsFollowing { get; private set; } = true;
    public bool CanReplay
    {
        get => canReplay;
        set => SetCapability(ref canReplay, value);
    }
    public bool CanEdit
    {
        get => canEdit;
        set => SetCapability(ref canEdit, value);
    }
    public bool CanExportAudio
    {
        get => canExportAudio;
        set => SetCapability(ref canExportAudio, value);
    }
    public bool CanRestoreMutedRows
    {
        get => canRestoreMutedRows;
        set => SetCapability(ref canRestoreMutedRows, value);
    }
    public event Action<string>? RenameSpeakerRequested;
    public event Action<string>? MarkMeRequested;
    public event Action? RemoveMeRequested;
    public event Action? SummarizeAboutMeRequested;
    public event Action<string?>? SpeakerColorRequested;
    public event Action<IReadOnlyList<TranscriptRow>>? SpeakerCorrectionRequested;
    public event Action<Exception>? OperationFailed;
    public event Action<PlaybackRange>? ReplayRequested;
    public event Action? ReplayPauseRequested;
    public event Action? ReplayStopRequested;
    public event Action<double>? ReplaySeekRequested;
    public event Action<IReadOnlyList<TranscriptRow>>? EditRequested;
    public event Action? ShowMutedRowsRequested;
    public event Action? ExportAudioRequested;
    public bool CanRecheckUnlabeled
    {
        get => canRecheckUnlabeled;
        set => SetCapability(ref canRecheckUnlabeled, value);
    }
    public event Action? RecheckUnlabeledRequested;
    public event Action<IReadOnlyList<TranscriptRow>, bool>? CorrectionRequested;
    public event Action<IReadOnlyList<TranscriptRow>>? OnlineTranscriptionRequested;

    public TranscriptView()
    {
        InitializeComponent();
        InitializeSearch();
        InitializeTextSelection();
        ReplayProgress.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            draggingPlayback = true;
            playbackDragStart = ReplayProgress.Value;
        }));
        ReplayProgress.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            draggingPlayback = false;
            if (e.Canceled)
                SetPlayback(playback);
            else if (ReplayProgress.Value != playbackDragStart && playback is not null)
                ReplaySeekRequested?.Invoke(ReplayProgress.Value);
        }));
        TranscriptActionsMenu.PlacementTarget = TranscriptActionsButton;
        Unloaded += (_, _) => TranscriptActionsMenu.IsOpen = false;
        RowsGrid.ItemsSource = rows;
        RowsGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scroll_Changed));
        CommandBindings.Add(new(ApplicationCommands.Copy, (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox { SelectionLength: > 0 } box)
                Copy(box.SelectedText);
            else
                Copy(GetSelectedText() ?? TranscriptPresentation.CopyText(SelectedRows()));
            e.Handled = true;
        }));
        UpdateSpeakerChoices();
        UpdateReplayControls();
    }

    public void SetDocument(TranscriptDocument next, bool reset = false)
    {
        document = next;
        if (reset)
        {
            CancelNavigationDrag();
            speakerFilter.ShowAll();
            SearchBox.Clear();
        }
        UpdateSpeakerChoices();
        if (!reset && Keyboard.FocusedElement is TranscriptTextBox { SelectionLength: > 0 } box && IsAncestorOf(box))
        {
            pending = next;
            UpdateReplayControls();
            RefreshRowPresentation();
            return;
        }
        pending = null;
        var previousRows = searchRows;
        // Search highlights use reference identity, so unchanged displayed rows must keep their instances.
        searchRows = speakerFilter.Apply(TranscriptPresentation.Rows(next))
            .Select((row, index) => index < previousRows.Count && row == previousRows[index] ? previousRows[index] : row).ToArray();
        if (reset)
            DisplayRows(searchRows, reset: true);
        ScheduleSearch(false);
    }

    private void DisplayRows(IReadOnlyList<TranscriptRow> nextRows, bool reset = false)
    {
        ReconcileTextSelection(nextRows, reset);
        var selectedKeys = reset ? [] : RowsGrid.SelectedItems.Cast<TranscriptRow>()
            .Select(row => (row.StartSeconds, row.SpeakerId)).ToHashSet();
        if (reset)
        {
            rows.Clear();
            IsFollowing = true;
        }
        scroll ??= FindChild<ScrollViewer>(RowsGrid);
        var offset = scroll?.VerticalOffset ?? 0;
        updating = true;
        for (var index = 0; index < nextRows.Count; index++)
        {
            if (index >= rows.Count)
                rows.Add(nextRows[index]);
            else if (rows[index] != nextRows[index])
                rows[index] = nextRows[index];
        }
        while (rows.Count > nextRows.Count)
            rows.RemoveAt(rows.Count - 1);
        foreach (var row in rows.Where(row => selectedKeys.Contains((row.StartSeconds, row.SpeakerId))))
        {
            if (!RowsGrid.SelectedItems.Contains(row))
                RowsGrid.SelectedItems.Add(row);
        }
        var navigationVersion = revealVersion;
        Dispatcher.BeginInvoke(() =>
        {
            scroll ??= FindChild<ScrollViewer>(RowsGrid);
            if (navigationVersion == revealVersion)
            {
                if (IsFollowing)
                    scroll?.ScrollToEnd();
                else
                    scroll?.ScrollToVerticalOffset(offset);
            }
            Dispatcher.BeginInvoke(() => updating = false, DispatcherPriority.ContextIdle);
        }, DispatcherPriority.Loaded);
        UpdateFollowLabel();
        NoMatchingSpeakers.Text = UiText.Get(!IsSearchFiltered ? "NoMatchingSpeakers" :
            searchFailed ? "SearchFailed" : searchReady ? "SearchNoMatches" : "SearchInProgress");
        NoMatchingSpeakers.Visibility = rows.Count == 0 && (!speakerFilter.IsAll || IsSearchFiltered) ? Visibility.Visible : Visibility.Collapsed;
        CopyVisibleAction.Header = UiText.Get(speakerFilter.IsAll && !IsSearchFiltered ? "CopyAllText" : "CopyFilteredText");
        UpdateReplayControls();
        RefreshRowPresentation();
    }

    public string AllText(bool annotated = false) => TranscriptPresentation.CopyText(TranscriptPresentation.Rows(document), annotated);
    public string VisibleText(bool annotated = false) =>
        TranscriptPresentation.CopyText(rows, annotated);

    public void SetSpeakerFilter(IEnumerable<string?>? speakerIds)
    {
        CancelNavigationDrag();
        if (speakerIds is null)
            speakerFilter.ShowAll();
        else
            speakerFilter.Select(speakerIds);
        RowsGrid.SelectedItems.Clear();
        pending = null;
        SetDocument(document);
    }

    private void UpdateSpeakerChoices()
    {
        var choices = new List<SpeakerChoice> { new(null, UiText.Get("UnlabeledSpeakers")) };
        choices.AddRange(document.Segments.Select(segment => segment.SpeakerId).OfType<string>().Distinct()
            .Select(id => new SpeakerChoice(id,
                (document.SpeakerName(id) == id ? id : $"{document.SpeakerName(id)} ({id})") +
                (document.Me?.SpeakerId == id ? $" ({UiText.Get("MeTag")})" : ""))));
        if (!speakerChoices.SequenceEqual(choices))
        {
            speakerChoices = choices;
            SpeakerFilterAction.Items.Clear();
            var all = new MenuItem { Header = UiText.Get("AllSpeakers"), IsCheckable = true, StaysOpenOnClick = true };
            all.Click += (_, _) => SetSpeakerFilter(all.IsChecked ? null : []);
            SpeakerFilterAction.Items.Add(all);
            SpeakerFilterAction.Items.Add(new Separator());
            foreach (var choice in choices)
            {
                var item = new MenuItem { Header = choice.Label, Tag = choice.Id, IsCheckable = true, StaysOpenOnClick = true };
                item.Click += (_, _) =>
                {
                    var selection = speakerChoices.Where(option => speakerFilter.Includes(option.Id)).Select(option => option.Id).ToHashSet();
                    if (item.IsChecked)
                        selection.Add(choice.Id);
                    else
                        selection.Remove(choice.Id);
                    SetSpeakerFilter(selection);
                };
                SpeakerFilterAction.Items.Add(item);
            }
        }
        var selected = speakerChoices.Where(choice => speakerFilter.Includes(choice.Id)).ToArray();
        SpeakerFilterText.Text = speakerFilter.IsAll ? UiText.Get("AllSpeakers") :
            selected.Length == 1 ? selected[0].Label : UiText.Format("SpeakerFilterCount", selected.Length);
        SpeakerFilterAction.ToolTip = ShortcutActions.Hint(Shortcuts, "speakerFilter") +
            (speakerFilter.IsAll ? "" : Environment.NewLine + string.Join(", ", selected.Select(choice => choice.Label)));
        CopyVisibleAction.Header = UiText.Get(speakerFilter.IsAll && !IsSearchFiltered ? "CopyAllText" : "CopyFilteredText");
        if (SpeakerFilterAction.Items.Count > 0)
        {
            ((MenuItem)SpeakerFilterAction.Items[0]).IsChecked = speakerFilter.IsAll;
            foreach (var item in SpeakerFilterAction.Items.OfType<MenuItem>().Skip(1))
                item.IsChecked = speakerFilter.Includes(item.Tag as string);
        }
    }

    private void TranscriptActions_Click(object sender, RoutedEventArgs e) => TranscriptActionsMenu.IsOpen = true;

    private void SpeakerFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SpeakerFilterAction))
            return;
        TranscriptActionsMenu.IsOpen = true;
        SpeakerFilterAction.IsSubmenuOpen = true;
    }

    public PlaybackRange SelectedPlaybackRange() =>
        PlaybackRange.FromRows(RowsGrid.SelectedItems.Cast<TranscriptRow>());

    public void SetPlayback(PlaybackSnapshot? snapshot)
    {
        playback = snapshot;
        ReplayTransport.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        if (snapshot is null)
        {
            draggingPlayback = false;
            return;
        }
        ReplayPauseButton.Content = UiText.Get(snapshot.IsPaused ? "ReplayResume" : "ReplayPause");
        ReplayPositionText.Text = UiText.Format("ReplayPosition",
            TranscriptDocument.Timestamp(draggingPlayback ? ReplayProgress.Value : snapshot.PositionSeconds),
            TranscriptDocument.Timestamp(snapshot.EndSeconds));
        ReplayTransport.ToolTip = UiText.Format("ReplayBounds",
            TranscriptDocument.Timestamp(snapshot.StartSeconds), TranscriptDocument.Timestamp(snapshot.EndSeconds));
        if (!draggingPlayback)
        {
            updatingPlayback = true;
            try
            {
                ReplayProgress.Minimum = snapshot.StartSeconds;
                ReplayProgress.Maximum = snapshot.EndSeconds;
                ReplayProgress.Value = Math.Clamp(snapshot.PositionSeconds, snapshot.StartSeconds, snapshot.EndSeconds);
            }
            finally { updatingPlayback = false; }
        }
    }

    private void ReplayPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingPlayback || playback is null)
            return;
        ReplayPositionText.Text = UiText.Format("ReplayPosition",
            TranscriptDocument.Timestamp(e.NewValue), TranscriptDocument.Timestamp(playback.EndSeconds));
        if (!draggingPlayback)
            ReplaySeekRequested?.Invoke(e.NewValue);
    }

    private void SetCapability(ref bool current, bool value)
    {
        if (current == value)
            return;
        current = value;
        UpdateReplayControls();
    }

    private void UpdateReplayControls()
    {
        if (PlayRangeAction is null || RowsGrid is null)
            return;
        CopyVisibleAction.IsEnabled = CopyAnnotatedAction.IsEnabled = FollowAction.IsEnabled = rows.Count > 0;
        SelectVisibleRowsMenu.IsEnabled = CopySelectedMenu.IsEnabled = CopySelectedAnnotatedMenu.IsEnabled =
            FollowMenu.IsEnabled = rows.Count > 0;
        PlayRangeAction.IsEnabled = CanReplay && RowsGrid.SelectedItems.Count > 0;
        PlayRangeMenu.IsEnabled = PlayRangeAction.IsEnabled;
        MuteRowsAction.IsEnabled = CanEdit && RowsGrid.SelectedItems.Count > 0;
        MuteRowsMenu.IsEnabled = MuteRowsAction.IsEnabled;
        ShowMutedRowsMenu.IsEnabled = CanRestoreMutedRows;
        ExportAudioMenu.IsEnabled = CanExportAudio;
        RecheckUnlabeledMenu.IsEnabled = CanRecheckUnlabeled;
        CorrectRowsAction.IsEnabled = CanCorrect && RowsGrid.SelectedItems.Count > 0;
        ReviewUnclearMenu.IsEnabled = CanReviewUnclear;
        CorrectRowsMenu.IsEnabled = CorrectRowsAction.IsEnabled;
        RetranscribeRowsMenu.IsEnabled = CorrectRowsAction.IsEnabled && CanReplay;
        OnlineRowsAction.IsEnabled = OnlineRowsMenu.IsEnabled = RetranscribeRowsMenu.IsEnabled;
        SpeakerColorMenu.IsEnabled = CanSetSpeakerColors && RowsGrid.SelectedItem is TranscriptRow;
        SpeakerColorMenu.Header = UiText.Get(RowsGrid.SelectedItem is TranscriptRow { SpeakerId: null }
            ? "SetUnlabeledSpeakerColor" : "SetSpeakerColor");
        RenameSpeakerMenu.IsEnabled = CanChangeSpeakers &&
            RowsGrid.SelectedItem is TranscriptRow { SpeakerId: not null };
        CorrectSpeakerMenu.IsEnabled = CanCorrect && RowsGrid.SelectedItems.Count > 0 && document.Segments.Any(segment => segment.SpeakerId is not null);
        MarkMeMenu.IsEnabled = RenameSpeakerMenu.IsEnabled;
        MarkMeMenu.Header = UiText.Get(RowsGrid.SelectedItem is TranscriptRow row && row.SpeakerId is not null &&
            document.Me?.SpeakerId == row.SpeakerId ? "EditMeTag" : "MarkAsMe");
        RemoveMeMenu.IsEnabled = CanChangeSpeakers && document.Me is not null;
        SummarizeAboutMeMenu.IsEnabled = CanSummarizeAboutMe && document.Me is not null;
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && textSelection is { } selection &&
            (RowsGrid.SelectedItems.Count != 1 || !RowsGrid.SelectedItems.Contains(selection.Row)))
            ClearTextSelection();
        UpdateReplayControls();
        RefreshRowPresentation();
    }
    private void CorrectRows_Click(object sender, RoutedEventArgs e) => RequestCorrection(false);
    private void ReviewUnclear_Click(object sender, RoutedEventArgs e)
    {
        if (CanReviewUnclear)
            ReviewUnclearRequested?.Invoke();
    }
    private void ShowMutedRows_Click(object sender, RoutedEventArgs e)
    {
        if (CanRestoreMutedRows)
            ShowMutedRowsRequested?.Invoke();
    }
    private void ExportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (CanExportAudio)
            ExportAudioRequested?.Invoke();
    }
    private void RecheckUnlabeled_Click(object sender, RoutedEventArgs e)
    {
        if (CanRecheckUnlabeled)
            RecheckUnlabeledRequested?.Invoke();
    }
    private void CorrectSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if (CorrectSpeakerMenu.IsEnabled)
            SpeakerCorrectionRequested?.Invoke(SelectedRows().ToArray());
    }
    private void RetranscribeRows_Click(object sender, RoutedEventArgs e) => RequestCorrection(true);
    private void OnlineRows_Click(object sender, RoutedEventArgs e)
    {
        if (OnlineRowsAction.IsEnabled)
            OnlineTranscriptionRequested?.Invoke(SelectedRows().ToArray());
    }

    private void RequestCorrection(bool reTranscribe)
    {
        try
        {
            if (!CanCorrect || (reTranscribe && !CanReplay))
                throw new InvalidOperationException(UiText.Get("CorrectionNeedsFinalTranscript"));
            var selected = RowsGrid.SelectedItems.Cast<TranscriptRow>().ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException(UiText.Get("SelectCorrectionRows"));
            CorrectionRequested?.Invoke(selected, reTranscribe);
        }
        catch (Exception ex) { OperationFailed?.Invoke(ex); }
    }
    private void PlayRange_Click(object sender, RoutedEventArgs e) => RequestReplay(RowsGrid.SelectedItems.Cast<TranscriptRow>());
    private void ReplayPause_Click(object sender, RoutedEventArgs e) => ReplayPauseRequested?.Invoke();
    private void ReplayStop_Click(object sender, RoutedEventArgs e) => ReplayStopRequested?.Invoke();
    private void SelectVisibleRows_Click(object sender, RoutedEventArgs e) => SelectAllVisibleRows();
    public void SelectAllVisibleRows() => RowsGrid.SelectAll();

    private void MuteRows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!CanEdit)
                throw new InvalidOperationException(UiText.Get("EditNeedsFinalTranscript"));
            var selected = RowsGrid.SelectedItems.Cast<TranscriptRow>().ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException(UiText.Get("SelectEditRows"));
            EditRequested?.Invoke(selected);
        }
        catch (Exception ex) { OperationFailed?.Invoke(ex); }
    }

    private void RequestReplay(IEnumerable<TranscriptRow> selection)
    {
        try
        {
            if (!CanReplay)
                throw new InvalidOperationException(UiText.Get("ReplayAfterStop"));
            ReplayRequested?.Invoke(PlaybackRange.FromRows(selection));
        }
        catch (Exception ex) { OperationFailed?.Invoke(ex); }
    }

    private void Grid_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not DataGridCell)
            element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element);
        if (element is DataGridCell { DataContext: TranscriptRow row } cell && cell.Column == TimeColumn)
        {
            RequestReplay([row]);
            e.Handled = true;
        }
        else if (element is DataGridCell { DataContext: TranscriptRow speaker } speakerCell &&
            speakerCell.Column == SpeakerColumn && CanSetSpeakerColors)
        {
            SpeakerColorRequested?.Invoke(speaker.SpeakerId);
            e.Handled = true;
        }
    }

    private IEnumerable<TranscriptRow> SelectedRows()
    {
        var selected = RowsGrid.SelectedItems.Cast<TranscriptRow>().ToHashSet();
        return selected.Count == 0 ? rows : rows.Where(selected.Contains);
    }

    private void Scroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        scroll ??= FindChild<ScrollViewer>(RowsGrid);
        if (!ReferenceEquals(e.OriginalSource, scroll))
            return;
        UpdateNavigationControls();
        if (updating || revealingSearchMatch || e.VerticalChange == 0)
            return;
        IsFollowing = scroll.ScrollableHeight - scroll.VerticalOffset <= 1;
        UpdateFollowLabel();
    }

    private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        CancelSearchNavigation();
        if (e.Delta > 0)
        {
            IsFollowing = false;
            UpdateFollowLabel();
        }
        else if (scroll is not null && scroll.ScrollableHeight - scroll.VerticalOffset <= 1)
        {
            IsFollowing = true;
            UpdateFollowLabel();
        }
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleTranscriptNavigation(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && string.IsNullOrEmpty(SearchBox.Text) && textSelection is not null)
        {
            ClearTextSelection();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down)
            CancelSearchNavigation();
        if (ShortcutInput.Matches(Shortcuts, "edit", ShortcutInput.EventKey(e), Keyboard.Modifiers))
        {
            RequestCorrection(false);
            e.Handled = true;
        }
        else if (e.Key is Key.PageUp or Key.Home or Key.Up)
        {
            IsFollowing = false;
            UpdateFollowLabel();
        }
    }

    private bool HandleTranscriptNavigation(Key key, ModifierKeys modifiers)
    {
        var action = new[] { "latest", "first", "pageUp", "pageDown" }
            .FirstOrDefault(id => ShortcutInput.Matches(Shortcuts, id, key, modifiers));
        return action is not null && NavigateTranscript(action);
    }

    public bool NavigateTranscript(string action)
    {
        if (action == "latest")
        {
            JumpToLatest();
            return true;
        }
        if (action == "first")
        {
            ClearTextSelection();
            CancelSearchNavigation();
            IsFollowing = false;
            if (Keyboard.FocusedElement is TranscriptTextBox selectedText)
                selectedText.Select(0, 0);
            RowsGrid.Focus();
            if (rows.Count > 0)
            {
                RowsGrid.SelectedItem = rows[0];
                RowsGrid.CurrentCell = new DataGridCellInfo(rows[0], TranscriptTextColumn);
                RowsGrid.ScrollIntoView(rows[0]);
            }
            scroll ??= FindChild<ScrollViewer>(RowsGrid);
            scroll?.ScrollToTop();
            RowsGrid.UpdateLayout();
            IsFollowing = false;
            UpdateFollowLabel();
            return true;
        }
        if (action is "pageUp" or "pageDown")
        {
            CancelSearchNavigation();
            IsFollowing = false;
            scroll ??= FindChild<ScrollViewer>(RowsGrid);
            if (action == "pageUp")
                scroll?.PageUp();
            else
                scroll?.PageDown();
            RowsGrid.UpdateLayout();
            IsFollowing = action == "pageDown" && scroll is not null && scroll.ScrollableHeight - scroll.VerticalOffset <= 1;
            UpdateFollowLabel();
            return true;
        }
        return false;
    }

    public void JumpToLatest()
    {
        ClearTextSelection();
        CancelSearchNavigation();
        IsFollowing = true;
        if (Keyboard.FocusedElement is TextBox box && IsAncestorOf(box))
            box.Select(box.Text.Length, 0);
        RowsGrid.Focus();
        if (pending is not null)
        {
            Keyboard.ClearFocus();
            SetDocument(pending);
        }
        if (rows.Count > 0)
        {
            RowsGrid.SelectedItem = rows[^1];
            RowsGrid.CurrentCell = new DataGridCellInfo(rows[^1], RowsGrid.Columns[^1]);
            RowsGrid.ScrollIntoView(rows[^1]);
        }
        scroll ??= FindChild<ScrollViewer>(RowsGrid);
        scroll?.ScrollToEnd();
        UpdateFollowLabel();
    }

    private void UpdateFollowLabel()
    {
        FollowAction.Header = UiText.Get(IsFollowing ? "FollowingTranscript" : "JumpToLatest");
        UpdateNavigationControls();
    }
    private void Follow_Click(object sender, RoutedEventArgs e) => JumpToLatest();
    private void CopyAll_Click(object sender, RoutedEventArgs e) => Copy(VisibleText());
    private void CopyAnnotated_Click(object sender, RoutedEventArgs e) => Copy(VisibleText(true));
    private void CopySelected_Click(object sender, RoutedEventArgs e) => Copy(GetSelectedText() ?? TranscriptPresentation.CopyText(SelectedRows()));
    private void CopySelectedAnnotated_Click(object sender, RoutedEventArgs e) => Copy(TranscriptPresentation.CopyText(SelectedRows(), true));
    private void RenameSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if (CanChangeSpeakers && RowsGrid.SelectedItem is TranscriptRow { SpeakerId: not null } row)
            RenameSpeakerRequested?.Invoke(row.SpeakerId);
    }
    private void MarkMe_Click(object sender, RoutedEventArgs e)
    {
        if (MarkMeMenu.IsEnabled && RowsGrid.SelectedItem is TranscriptRow { SpeakerId: not null } row)
            MarkMeRequested?.Invoke(row.SpeakerId);
    }
    private void RemoveMe_Click(object sender, RoutedEventArgs e)
    {
        if (RemoveMeMenu.IsEnabled)
            RemoveMeRequested?.Invoke();
    }
    private void SummarizeAboutMe_Click(object sender, RoutedEventArgs e)
    {
        if (SummarizeAboutMeMenu.IsEnabled)
            SummarizeAboutMeRequested?.Invoke();
    }
    private void SpeakerColor_Click(object sender, RoutedEventArgs e)
    {
        if (CanSetSpeakerColors && RowsGrid.SelectedItem is TranscriptRow row)
            SpeakerColorRequested?.Invoke(row.SpeakerId);
    }
    private void Text_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (pending is not null)
                SetDocument(pending);
        }, DispatcherPriority.Input);
    private void Copy(string value)
    {
        if (value.Length == 0)
            return;
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.COMException ex) { OperationFailed?.Invoke(ex); }
    }
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(RowsGrid, source) is DataGridRow { IsSelected: false } row)
            RowsGrid.SelectedItem = row.Item;
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private sealed record SpeakerChoice(string? Id, string Label);
}
