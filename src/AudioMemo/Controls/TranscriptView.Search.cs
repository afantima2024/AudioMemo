using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Controls;

public partial class TranscriptView
{
    public static readonly DependencyProperty SelectionBackgroundProperty = DependencyProperty.Register(
        nameof(SelectionBackground), typeof(Brush), typeof(TranscriptView),
        new PropertyMetadata(Brushes.Transparent, PaletteChanged));
    public static readonly DependencyProperty SelectionForegroundProperty = DependencyProperty.Register(
        nameof(SelectionForeground), typeof(Brush), typeof(TranscriptView),
        new PropertyMetadata(Brushes.Black, PaletteChanged));
    public Brush SelectionBackground { get => (Brush)GetValue(SelectionBackgroundProperty); set => SetValue(SelectionBackgroundProperty, value); }
    public Brush SelectionForeground { get => (Brush)GetValue(SelectionForegroundProperty); set => SetValue(SelectionForegroundProperty, value); }
    private readonly TranscriptSearchPanel searchPanel = new();
    private Window? searchWindow;
    private TextBox SearchBox => searchPanel.SearchBox;
    private CheckBox FuzzySearchCheck => searchPanel.FuzzySearchCheck;
    private TextBlock SearchStatus => searchPanel.SearchStatus;
    private Button SearchNextButton => searchPanel.SearchNextButton;
    private Button SearchPreviousButton => searchPanel.SearchPreviousButton;
    private Button ClearSearchButton => searchPanel.ClearSearchButton;
    private IReadOnlyList<TranscriptRow> searchRows = [];
    private bool searchReady;
    private bool searchFailed;
    public bool IsSearchFiltered => searchPanel.OnlyMatchesCheck.IsChecked == true && !string.IsNullOrWhiteSpace(SearchBox.Text);
    public event Action? SearchRequested;
    private DispatcherTimer? searchDelay;
    private CancellationTokenSource? searchOperation;
    private IReadOnlyList<TranscriptMatch> searchMatches = [];
    private IReadOnlyList<TranscriptRow> searchedRows = [];
    private Dictionary<TranscriptRow, IReadOnlyList<TranscriptMatch>> matchesByRow = [];
    private int searchIndex = -1;
    private long searchVersion;
    private long revealVersion;
    private bool revealingSearchMatch;
    private bool navigateAfterSearch;
    private SearchAnchor? searchAnchor;

    private void InitializeSearch()
    {
        SetResourceReference(SelectionBackgroundProperty, "TranscriptSelectionBackground");
        SetResourceReference(SelectionForegroundProperty, "TranscriptSelectionForeground");
        SearchBox.TextChanged += Search_TextChanged;
        FuzzySearchCheck.Checked += Search_ModeChanged;
        FuzzySearchCheck.Unchecked += Search_ModeChanged;
        SearchNextButton.Click += SearchNext_Click;
        SearchPreviousButton.Click += SearchPrevious_Click;
        ClearSearchButton.Click += SearchClear_Click;
        searchPanel.OnlyMatchesCheck.Checked += SearchFilter_Changed;
        searchPanel.OnlyMatchesCheck.Unchecked += SearchFilter_Changed;
        searchPanel.PreviewKeyDown += Search_PreviewKeyDown;
        searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        searchDelay.Tick += async (_, _) => await RunSearchAsync();
        PreviewKeyDown += Search_PreviewKeyDown;
        RowsGrid.PreviewMouseDown += (_, _) => CancelSearchNavigation();
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= Appearance_Changed;
            SystemParameters.StaticPropertyChanged += Appearance_Changed;
            ScheduleSearch(false);
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= Appearance_Changed;
            searchVersion++;
            searchDelay.Stop();
            searchOperation?.Cancel();
            searchWindow?.Close();
        };
        UpdateSearchControls();
    }

    private static void PaletteChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (TranscriptView)sender;
        if (view.RowsGrid is null)
            return;
        view.RowsGrid.Resources["DataGridRowSelectedBackgroundThemeBrush"] = view.SelectionBackground;
        view.RowsGrid.Resources["DataGridRowSelectedForegroundThemeBrush"] = view.SelectionForeground;
        view.RefreshRowPresentation();
    }

    private void OpenSearch_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke();

    public void ShowSearchWindow(Window owner)
    {
        var window = CreateSearchWindow(owner);
        window.Show();
        window.Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private Window CreateSearchWindow(Window? owner)
    {
        if (searchWindow is not null)
            return searchWindow;
        var window = new Window
        {
            Owner = owner,
            Content = searchPanel,
            Width = 550,
            MinWidth = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.SetBinding(Window.TitleProperty, new Binding("[SearchTranscript]") { Source = UiText.Current });
        window.Closed += (_, _) =>
        {
            window.Content = null;
            searchWindow = null;
        };
        searchWindow = window;
        return window;
    }

    private void Appearance_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.BeginInvoke(() =>
            {
                if (IsLoaded)
                    RefreshRowPresentation();
            }, DispatcherPriority.DataBind);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ScheduleSearch(true);
    private void Search_ModeChanged(object sender, RoutedEventArgs e) => ScheduleSearch(true);

    private void SearchFilter_Changed(object sender, RoutedEventArgs e)
    {
        CancelSearchNavigation();
        IsFollowing = false;
        DisplaySearchRows();
        if (searchReady && searchIndex >= 0)
            RevealSearchMatch();
    }

    private void DisplaySearchRows()
    {
        if (IsSearchFiltered)
        {
            var matching = searchMatches.Select(match => match.RowIndex).ToHashSet();
            DisplayRows(searchReady ? searchedRows.Where((_, index) => matching.Contains(index)).ToArray() : []);
        }
        else
            DisplayRows(searchRows);
    }

    private void ScheduleSearch(bool navigate)
    {
        if (searchDelay is null)
            return;
        searchVersion++;
        searchDelay.Stop();
        searchOperation?.Cancel();
        navigateAfterSearch |= navigate;
        if (navigate)
            searchAnchor = null;
        searchMatches = [];
        matchesByRow.Clear();
        searchIndex = -1;
        searchReady = false;
        searchFailed = false;
        DisplaySearchRows();
        var hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        SearchStatus.Text = hasQuery ? UiText.Get("SearchInProgress") : "";
        SearchStatus.ToolTip = UiText.Get("SearchHint");
        if (hasQuery)
            searchDelay.Start();
        else
        {
            searchedRows = [];
            searchAnchor = null;
            navigateAfterSearch = false;
        }
        UpdateSearchControls();
    }

    private async Task RunSearchAsync()
    {
        searchDelay?.Stop();
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
            return;
        var version = searchVersion;
        var snapshot = searchRows.ToArray();
        var fuzzy = FuzzySearchCheck.IsChecked == true;
        var navigate = navigateAfterSearch;
        var navigationVersion = revealVersion;
        navigateAfterSearch = false;
        using var cancellation = new CancellationTokenSource();
        searchOperation?.Cancel();
        searchOperation = cancellation;
        try
        {
            var found = await Task.Run(() => TranscriptSearch.Find(snapshot, query, fuzzy, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || version != searchVersion)
                return;
            searchedRows = snapshot;
            searchMatches = found;
            searchReady = true;
            matchesByRow = new(ReferenceEqualityComparer.Instance);
            foreach (var group in found.GroupBy(match => match.RowIndex))
                matchesByRow.Add(snapshot[group.Key], group.ToArray());
            searchIndex = found.Count == 0 ? -1 : 0;
            if (!navigate && searchAnchor is { } anchor)
            {
                var closest = found.Select((match, index) => (Match: match, Index: index))
                    .Where(item => snapshot[item.Match.RowIndex].StartSeconds == anchor.RowStart &&
                        snapshot[item.Match.RowIndex].SpeakerId == anchor.SpeakerId)
                    .OrderBy(item => Math.Abs(item.Match.Start - anchor.Start)).FirstOrDefault();
                if (closest.Match is not null)
                    searchIndex = closest.Index;
            }
            DisplaySearchRows();
            UpdateSearchStatus();
            if (navigate && searchIndex >= 0 && navigationVersion == revealVersion)
                RevealSearchMatch();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == searchVersion)
                ReportSearchError(ex);
        }
        finally
        {
            if (ReferenceEquals(searchOperation, cancellation))
                searchOperation = null;
            UpdateSearchControls();
        }
    }

    private void ReportSearchError(Exception error)
    {
        searchFailed = true;
        DisplaySearchRows();
        var inline = searchWindow?.IsVisible == true || OperationFailed is null;
        SearchStatus.Text = inline ? UiText.Format("ErrorDetails", error.Message) : UiText.Get("SearchFailed");
        SearchStatus.ToolTip = inline ? SearchStatus.Text : UiText.Get("SearchHint");
        if (!inline)
            OperationFailed?.Invoke(error);
    }

    private void CancelSearchNavigation()
    {
        revealVersion++;
        revealingSearchMatch = false;
        navigateAfterSearch = false;
    }

    private void UpdateSearchStatus()
    {
        SearchStatus.Text = searchMatches.Count == 0 ? UiText.Get("SearchNoMatches") :
            UiText.Format("SearchMatches", searchIndex + 1, searchMatches.Count);
        SearchStatus.ToolTip = SearchStatus.Text + Environment.NewLine + UiText.Get("SearchHint");
        if (searchIndex >= 0)
        {
            var match = searchMatches[searchIndex];
            var row = searchedRows[match.RowIndex];
            searchAnchor = new(row.StartSeconds, row.SpeakerId, match.Start);
        }
        UpdateSearchControls();
    }

    private void UpdateSearchControls()
    {
        if (SearchNextButton is null)
            return;
        SearchNextButton.IsEnabled = SearchPreviousButton.IsEnabled = searchMatches.Count > 0;
        ClearSearchButton.IsEnabled = SearchBox.Text.Length > 0;
    }

    private void SearchPrevious_Click(object sender, RoutedEventArgs e) => MoveSearchMatch(-1);
    private void SearchNext_Click(object sender, RoutedEventArgs e) => MoveSearchMatch(1);
    private void SearchClear_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();

    private void MoveSearchMatch(int direction)
    {
        if (searchMatches.Count == 0)
            return;
        searchIndex = (searchIndex + direction + searchMatches.Count) % searchMatches.Count;
        UpdateSearchStatus();
        RevealSearchMatch();
    }

    private void RevealSearchMatch()
    {
        var match = searchMatches[searchIndex];
        var row = searchedRows[match.RowIndex];
        var version = ++revealVersion;
        var queryVersion = searchVersion;
        revealingSearchMatch = true;
        IsFollowing = false;
        UpdateFollowLabel();
        RowsGrid.SelectedItem = row;
        RowsGrid.CurrentCell = new DataGridCellInfo(row, TranscriptTextColumn);
        RowsGrid.ScrollIntoView(row, TranscriptTextColumn);
        RowsGrid.UpdateLayout();
        RefreshRowPresentation();
        // Wait for WPF's row anchoring to finish before revealing text inside a tall row.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (version != revealVersion || queryVersion != searchVersion || !ReferenceEquals(RowsGrid.SelectedItem, row))
                    return;
                scroll ??= FindChild<ScrollViewer>(RowsGrid);
                if (scroll is not null && FindChild<ScrollContentPresenter>(scroll) is { } viewport &&
                    TranscriptTextColumn.GetCellContent(row) is { } content &&
                    FindChild<TranscriptTextBox>(content) is { } box &&
                    box.MatchRectangles(match).FirstOrDefault() is var rectangle && !rectangle.IsEmpty && rectangle.Height > 0)
                {
                    var visible = box.TransformToAncestor(viewport).TransformBounds(rectangle);
                    var delta = visible.Top < 0 ? visible.Top : Math.Max(0, visible.Bottom - viewport.ActualHeight);
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + delta);
                    RowsGrid.UpdateLayout();
                }
            }
            catch (Exception ex) { ReportSearchError(ex); }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (version == revealVersion)
                        revealingSearchMatch = false;
                }, DispatcherPriority.ContextIdle);
            }
        }, DispatcherPriority.ContextIdle);
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ReferenceEquals(sender, searchPanel))
            GlobalShortcutRequested?.Invoke(e);
        if (e.Handled)
            return;
        var key = ShortcutInput.EventKey(e);
        if (HandleSearchKey(key, Keyboard.Modifiers, SearchBox.IsKeyboardFocusWithin) ||
            (ReferenceEquals(sender, this) && HandleTranscriptNavigation(key, Keyboard.Modifiers)))
            e.Handled = true;
    }

    private bool HandleSearchKey(Key key, ModifierKeys modifiers, bool searchFocused)
    {
        if (ShortcutInput.Matches(Shortcuts, "search", key, modifiers))
        {
            SearchRequested?.Invoke();
            SearchBox.Focus();
            SearchBox.SelectAll();
            return true;
        }
        var enterNavigation = key == Key.Enter && searchFocused && modifiers is ModifierKeys.None or ModifierKeys.Shift;
        if (ShortcutInput.Matches(Shortcuts, "searchNext", key, modifiers) ||
            ShortcutInput.Matches(Shortcuts, "searchPrevious", key, modifiers) || enterNavigation)
        {
            MoveSearchMatch(ShortcutInput.Matches(Shortcuts, "searchPrevious", key, modifiers) ||
                (enterNavigation && modifiers == ModifierKeys.Shift) ? -1 : 1);
            return true;
        }
        if (ShortcutInput.Matches(Shortcuts, "searchClear", key, modifiers) && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            return true;
        }
        return false;
    }

    private void Rows_LoadingRow(object sender, DataGridRowEventArgs e) => ApplyRowPresentation(e.Row);
    private void TextCell_Loaded(object sender, RoutedEventArgs e)
    {
        var box = (TranscriptTextBox)sender;
        box.ContentUpdated -= TextCell_ContentUpdated;
        box.ContentUpdated += TextCell_ContentUpdated;
        ApplyTextMatches(box);
        RestoreTextSelection(box);
    }

    private void TextCell_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (TranscriptTextBox)sender;
        box.ContentUpdated -= TextCell_ContentUpdated;
        box.ContentUpdated += TextCell_ContentUpdated;
        ApplyTextMatches(box);
        RestoreTextSelection(box);
    }

    private void ApplyRowPresentation(DataGridRow row)
    {
        var speakerBrush = SpeakerColors.ForSpeaker((row.Item as TranscriptRow)?.SpeakerId, document.SpeakerColors,
            SystemParameters.HighContrast, document.UnlabeledSpeakerColor);
        row.Resources["SpeakerSwatchBrush"] = speakerBrush;
        if (row.IsSelected)
        {
            row.Background = SelectionBackground;
            row.Foreground = SelectionForeground;
        }
        else
        {
            row.Background = speakerBrush;
            var color = ((SolidColorBrush)row.Background).Color;
            row.Foreground = color.A == 0
                ? AppAppearance.Brush(AppAppearance.Rgb(((SolidColorBrush)FindResource("TextFillColorPrimaryBrush")).Color))
                : AppAppearance.Brush(AppAppearance.Rgb(color).ReadableText);
        }
        foreach (var box in VisualChildren<TranscriptTextBox>(row))
            ApplyTextMatches(box);
    }

    private void ApplyTextMatches(TranscriptTextBox box)
    {
        var found = box.DataContext is TranscriptRow row && matchesByRow.TryGetValue(row, out var matches) ? matches : [];
        box.SetMatches(found, searchIndex >= 0 ? searchMatches[searchIndex] : null);
    }

    private void RefreshRowPresentation()
    {
        foreach (var row in VisualChildren<DataGridRow>(RowsGrid))
            ApplyRowPresentation(row);
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found)
                yield return found;
            foreach (var nested in VisualChildren<T>(child))
                yield return nested;
        }
    }

    private sealed record SearchAnchor(double RowStart, string? SpeakerId, int Start);
}
