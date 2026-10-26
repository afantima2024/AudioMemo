using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    private Task? libraryStatisticsTask;
    private CancellationTokenSource? libraryStatisticsCancellation;
    private TimeSpan? lastStatisticsRead;
    private long statisticsVersion;
    private string? statisticsError;

    private void StatusBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (StatusIndicators is not null)
            StatusIndicators.MaxWidth = Math.Max(120, StatusBarBorder.ActualWidth - 28 - 180);
    }

    private void StatusBarMenu_Opened(object sender, RoutedEventArgs e) => SynchronizeStatusOptions();

    private void SynchronizeStatusOptions()
    {
        foreach (var item in StatusBarMenu.Items.OfType<MenuItem>().Where(item => item.Tag is string))
            item.IsChecked = settings.StatusBar.Get((string)item.Tag);
    }

    private void StatusOption_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (closing)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            var item = (MenuItem)sender;
            var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
                ?? throw new InvalidOperationException("Could not copy status bar settings.");
            next.StatusBar.Set((string)item.Tag, item.IsChecked);
            storage.SaveSettings(next);
            settings = next;
            UpdateStatusInformation(invalidate: true);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SynchronizeStatusOptions(); }
    }

    private void UpdateStatusInformation(bool invalidate = false)
    {
        if (StatusIndicators is null)
            return;
        var options = settings.StatusBar;
        StatusText.Visibility = options.Messages ? Visibility.Visible : Visibility.Collapsed;
        ModelStatusText.Visibility = options.Llm ? Visibility.Visible : Visibility.Collapsed;
        SpeechModelStatusText.Visibility = options.SpeechModel ? Visibility.Visible : Visibility.Collapsed;
        LibrarySizeStatusText.Visibility = options.LibrarySize ? Visibility.Visible : Visibility.Collapsed;
        RecordingCountStatusText.Visibility = options.RecordingCount ? Visibility.Visible : Visibility.Collapsed;
        FreeDiskStatusText.Visibility = options.FreeDiskSpace ? Visibility.Visible : Visibility.Collapsed;
        if (options.RecordingCount)
            RecordingCountStatusText.Text = UiText.Format("StatusRecordingCount", HistoryList.Items.Count);
        if (invalidate)
        {
            statisticsVersion++;
            lastStatisticsRead = null;
            libraryStatisticsCancellation?.Cancel();
            LibrarySizeStatusText.Text = UiText.Format("StatusLibrarySize", UiText.Get("StatusCalculating"));
            FreeDiskStatusText.Text = UiText.Format("StatusFreeDisk", UiText.Get("StatusCalculating"));
        }
        if (!initialized || closing || !options.LibrarySize && !options.FreeDiskSpace ||
            libraryStatisticsTask is { IsCompleted: false } ||
            lastStatisticsRead is { } last && historyPeekClock.Elapsed - last < TimeSpan.FromSeconds(30))
            return;
        lastStatisticsRead = historyPeekClock.Elapsed;
        libraryStatisticsTask = ReadLibraryStatisticsAsync(statisticsVersion, settings.SaveFolder,
            HistoryList.Items.OfType<RecordingSession>().Select(session => session.Folder).ToArray(),
            options.LibrarySize, options.FreeDiskSpace);
    }

    private async Task ReadLibraryStatisticsAsync(long version, string folder, IReadOnlyList<string> sessions, bool occupied, bool free)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        libraryStatisticsCancellation = cancellation;
        try
        {
            var statistics = await Task.Run(() => LibraryStatistics.Read(folder, sessions, occupied, free, cancellation.Token),
                cancellation.Token);
            if (closing || version != statisticsVersion)
                return;
            if (statistics.OccupiedBytes is { } size)
                LibrarySizeStatusText.Text = UiText.Format("StatusLibrarySize", LibraryStatistics.FormatBytes(size, UiText.Current.Culture));
            if (statistics.FreeBytes is { } available)
                FreeDiskStatusText.Text = UiText.Format("StatusFreeDisk", LibraryStatistics.FormatBytes(available, UiText.Current.Culture));
            LibrarySizeStatusText.ToolTip = UiText.Get("StatusLibrarySizeHint");
            FreeDiskStatusText.ToolTip = folder + Environment.NewLine + FreeDiskStatusText.Text;
            statisticsError = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closing || version != statisticsVersion)
                return;
            LibrarySizeStatusText.Text = UiText.Format("StatusLibrarySize", UiText.Get("StatusUnavailable"));
            FreeDiskStatusText.Text = UiText.Format("StatusFreeDisk", UiText.Get("StatusUnavailable"));
            LibrarySizeStatusText.ToolTip = FreeDiskStatusText.ToolTip = ex.Message;
            if (statisticsError != ex.Message)
                SetStatus(UiText.Format("StatusStatisticsFailed", ex.Message), true);
            statisticsError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(libraryStatisticsCancellation, cancellation))
                libraryStatisticsCancellation = null;
        }
    }
}
