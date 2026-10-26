using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo;

public partial class MainWindow
{
    private enum ReadingLayout { Wide, Stacked, Compact }
    private (ReadingLayout Layout, bool MainPoints, bool Notes)? readingLayout;
    private string readingPane = "transcript";
    private bool CompactHistory => WorkspaceBody is { ActualWidth: > 0 and < 900 };
    private bool HistoryPinned => settings.HistoryVisible && !CompactHistory;

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWorkspaceLayout();

    private void SaveTranscriptNavigationPosition(Point position)
    {
        if (closing)
            throw new InvalidOperationException(UiText.Get("SessionBusy"));
        var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        next.TranscriptNavigationX = position.X;
        next.TranscriptNavigationY = position.Y;
        storage.SaveSettings(next);
        settings = next;
    }

    private void InitializePaneChoices()
    {
        if (!PaneVisible(readingPane))
            readingPane = "transcript";
        CompactPaneBox.ItemsSource = new[]
        {
            new LanguageOption("transcript", UiText.Get("Transcript")),
            new LanguageOption("summary", UiText.Get("MainPoints")),
            new LanguageOption("notes", UiText.Get("MyNotes"))
        }.Where(option => PaneVisible(option.Code)).ToArray();
        CompactPaneBox.SelectedValue = readingPane;
        MainPointsPanelMenu.IsChecked = settings.MainPointsVisible;
        MyNotesPanelMenu.IsChecked = settings.MyNotesVisible;
        PanelsButton.Visibility = settings.MainPointsVisible && settings.MyNotesVisible ? Visibility.Collapsed : Visibility.Visible;
        UpdateApplicationMenu();
    }

    private void ClosePane_Click(object sender, RoutedEventArgs e) =>
        SetPaneVisibility((string)((Button)sender).Tag, false);

    private void PaneVisibility_Click(object sender, RoutedEventArgs e) =>
        SetPaneVisibility((string)((MenuItem)sender).Tag, ((MenuItem)sender).IsChecked);

    private void ShowPanels_Click(object sender, RoutedEventArgs e)
    {
        InitializePaneChoices();
        PanelsButton.ContextMenu.PlacementTarget = PanelsButton;
        PanelsButton.ContextMenu.Placement = PlacementMode.Bottom;
        PanelsButton.ContextMenu.IsOpen = true;
    }

    private void SetPaneVisibility(string pane, bool visible)
    {
        try
        {
            if (closing)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            if (pane == "notes" && !visible)
                SaveNotes();
            var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            if (pane == "summary")
                next.MainPointsVisible = visible;
            else if (pane == "notes")
                next.MyNotesVisible = visible;
            else
                throw new ArgumentOutOfRangeException(nameof(pane));
            storage.SaveSettings(next);
            settings = next;
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            InitializePaneChoices();
            UpdateWorkspaceLayout();
        }
    }

    private bool PaneVisible(string pane) => pane switch
    {
        "transcript" => true,
        "summary" => settings.MainPointsVisible,
        "notes" => settings.MyNotesVisible,
        _ => false
    };

    private void CompactPane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompactPaneBox.SelectedValue is not string pane)
            return;
        readingPane = pane;
        UpdateWorkspaceLayout();
    }

    private void ShowWorkspacePane(string pane, bool focus = false)
    {
        if (!PaneVisible(pane))
            return;
        readingPane = pane;
        CompactPaneBox.SelectedValue = pane;
        UpdateWorkspaceLayout();
        if (focus)
        {
            if (pane == "transcript")
                TranscriptPanel.FocusTranscript();
            else if (pane == "summary")
                SummaryBox.Focus();
            else
                NotesBox.Focus();
        }
    }

    private void UpdateWorkspaceLayout()
    {
        if (WorkspaceContent is null || ReadingWorkspace is null || CaptureCommands is null || WorkspaceContent.ActualWidth <= 0)
            return;
        UpdateHistoryVisibility();
        var compactHeader = WorkspaceBody.ActualWidth < 900;
        WorkspaceScroller.Margin = new Thickness(compactHeader ? 8 : 12, 8, compactHeader ? 8 : 12, 8);
        var compactToolbar = HeaderControls.ActualWidth < 1050;
        MainProviderBox.Width = compactToolbar ? 145 : 190;
        MainModelBox.Width = compactToolbar ? 170 : 245;
        ProviderLabel.Visibility = compactToolbar ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Visibility = HeaderControls.ActualWidth < 740 ? Visibility.Collapsed : Visibility.Visible;
        BrandText.FontSize = compactToolbar ? 18 : 21;
        BrandText.Margin = new Thickness(0, 0, compactToolbar ? 12 : 22, 0);
        var narrowControls = WorkspaceContent.ActualWidth < 950;
        Grid.SetColumnSpan(DeviceSelectors, narrowControls ? 2 : 1);
        Grid.SetColumn(CaptureCommands, narrowControls ? 0 : 1);
        Grid.SetRow(CaptureCommands, narrowControls ? 1 : 0);
        Grid.SetColumnSpan(CaptureCommands, narrowControls ? 2 : 1);
        CaptureCommands.Margin = narrowControls ? new Thickness(0, 6, 0, 0) : new Thickness(8, 0, 0, 0);

        var layout = WorkspaceContent.ActualHeight < 490 && WorkspaceContent.ActualWidth < 900
            ? ReadingLayout.Compact : WorkspaceContent.ActualWidth < 820 ? ReadingLayout.Stacked : ReadingLayout.Wide;
        var compact = layout == ReadingLayout.Compact;
        var hasSidePane = settings.MainPointsVisible || settings.MyNotesVisible;
        var bothSidePanes = settings.MainPointsVisible && settings.MyNotesVisible;
        if (!PaneVisible(readingPane))
        {
            readingPane = "transcript";
            CompactPaneBox.SelectedValue = readingPane;
        }
        CompactPanePicker.Visibility = compact && hasSidePane ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceTitle.Visibility = WorkspaceSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TranscriptCard.Padding = MainPointsCard.Padding = PersonalNotesCard.Padding = new Thickness(compact ? 6 : 10);
        TranscriptCard.Visibility = !compact || readingPane == "transcript" ? Visibility.Visible : Visibility.Collapsed;
        NotesPane.Visibility = hasSidePane && (!compact || readingPane != "transcript") ? Visibility.Visible : Visibility.Collapsed;
        MainPointsCard.Visibility = settings.MainPointsVisible && (!compact || readingPane == "summary") ? Visibility.Visible : Visibility.Collapsed;
        PersonalNotesCard.Visibility = settings.MyNotesVisible && (!compact || readingPane == "notes") ? Visibility.Visible : Visibility.Collapsed;
        ReadingDivider.Visibility = !compact && hasSidePane ? Visibility.Visible : Visibility.Collapsed;
        NotesDivider.Visibility = !compact && bothSidePanes ? Visibility.Visible : Visibility.Collapsed;
        var layoutKey = (layout, settings.MainPointsVisible, settings.MyNotesVisible);
        if (readingLayout == layoutKey)
            return;
        readingLayout = layoutKey;
        if (compact || !hasSidePane)
        {
            ReadingWorkspace.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            ReadingWorkspace.ColumnDefinitions[1].Width = ReadingWorkspace.ColumnDefinitions[2].Width = new GridLength(0);
            ReadingWorkspace.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            ReadingWorkspace.RowDefinitions[1].Height = ReadingWorkspace.RowDefinitions[2].Height = new GridLength(0);
            NotesPane.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            NotesPane.ColumnDefinitions[1].Width = NotesPane.ColumnDefinitions[2].Width = new GridLength(0);
            NotesPane.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            NotesPane.RowDefinitions[1].Height = NotesPane.RowDefinitions[2].Height = new GridLength(0);
            Grid.SetColumn(NotesPane, 0);
            Grid.SetRow(NotesPane, 0);
            Grid.SetColumn(PersonalNotesCard, 0);
            Grid.SetRow(PersonalNotesCard, 0);
            return;
        }
        var stacked = layout == ReadingLayout.Stacked;
        ReadingWorkspace.ColumnDefinitions[0].Width = new GridLength(stacked ? 1 : 2.2, GridUnitType.Star);
        ReadingWorkspace.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 8);
        ReadingWorkspace.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ReadingWorkspace.RowDefinitions[0].Height = new GridLength(stacked ? 2 : 1, GridUnitType.Star);
        ReadingWorkspace.RowDefinitions[1].Height = new GridLength(stacked ? 8 : 0);
        ReadingWorkspace.RowDefinitions[2].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(NotesPane, stacked ? 0 : 2);
        Grid.SetRow(NotesPane, stacked ? 2 : 0);
        ArrangeDivider(ReadingDivider, stacked);

        if (!bothSidePanes)
        {
            NotesPane.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            NotesPane.ColumnDefinitions[1].Width = NotesPane.ColumnDefinitions[2].Width = new GridLength(0);
            NotesPane.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            NotesPane.RowDefinitions[1].Height = NotesPane.RowDefinitions[2].Height = new GridLength(0);
            Grid.SetRow(PersonalNotesCard, 0);
            Grid.SetColumn(PersonalNotesCard, 0);
            return;
        }

        NotesPane.ColumnDefinitions[0].Width = new GridLength(stacked ? 2 : 1, GridUnitType.Star);
        NotesPane.ColumnDefinitions[1].Width = new GridLength(stacked ? 8 : 0);
        NotesPane.ColumnDefinitions[2].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        NotesPane.RowDefinitions[0].Height = new GridLength(stacked ? 1 : 2, GridUnitType.Star);
        NotesPane.RowDefinitions[1].Height = new GridLength(stacked ? 0 : 8);
        NotesPane.RowDefinitions[2].Height = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(PersonalNotesCard, stacked ? 0 : 2);
        Grid.SetColumn(PersonalNotesCard, stacked ? 2 : 0);
        ArrangeDivider(NotesDivider, !stacked);
    }

    private static void ArrangeDivider(GridSplitter divider, bool horizontal)
    {
        Grid.SetRow(divider, horizontal ? 1 : 0);
        Grid.SetColumn(divider, horizontal ? 0 : 1);
        divider.ResizeDirection = horizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        divider.Width = horizontal ? double.NaN : 6;
        divider.Height = horizontal ? 6 : double.NaN;
        divider.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        divider.VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch;
    }
}
