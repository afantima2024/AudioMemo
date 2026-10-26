using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo;

internal sealed class MessageHistoryWindow : Window
{
    private readonly MessageHistoryStore history;
    private readonly ObservableCollection<MessageEntry> entries = [];
    private readonly HashSet<Guid> seen = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly DataGrid list = new();
    private readonly TextBox details = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button older = new() { Content = UiText.Get("MessageHistoryOlder"), IsEnabled = false };
    private long? before;
    private long generation;
    private bool reading;
    private bool closed;
    private int unreadable;
    private string? readError;
    internal MessageHistoryStore MessageHistory => history;

    internal MessageHistoryWindow(Window? owner, MessageHistoryStore history)
    {
        Owner = owner;
        this.history = history;
        Title = UiText.Get("MessageHistory");
        Width = 1000; Height = 650; MinWidth = 540; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(2, GridUnitType.Star), new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = height });
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var clear = new Button { Content = UiText.Get("MessageHistoryClear"), Margin = new Thickness(8, 0, 0, 0) };
        clear.Click += Clear_Click;
        DockPanel.SetDock(clear, Dock.Right);
        heading.Children.Add(clear);
        heading.Children.Add(new TextBlock { Text = UiText.Get("MessageHistoryHint"), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(heading);
        list.AutoGenerateColumns = false;
        list.IsReadOnly = true;
        list.CanUserAddRows = list.CanUserDeleteRows = false;
        list.SelectionMode = DataGridSelectionMode.Single;
        list.HeadersVisibility = DataGridHeadersVisibility.Column;
        list.ItemsSource = entries;
        list.Columns.Add(new DataGridTextColumn { Header = UiText.Get("MessageWhen"), Binding = new Binding(nameof(MessageEntry.TimeLabel)), Width = 160 });
        list.Columns.Add(new DataGridTextColumn { Header = UiText.Get("MessageType"), Binding = new Binding(nameof(MessageEntry.LevelLabel)), Width = 90 });
        list.Columns.Add(new DataGridTextColumn { Header = UiText.Get("MessageWhy"), Binding = new Binding(nameof(MessageEntry.Reason)), Width = 180 });
        list.Columns.Add(new DataGridTextColumn { Header = UiText.Get("MessageWhat"), Binding = new Binding(nameof(MessageEntry.What)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        list.SelectionChanged += (_, _) => details.Text = list.SelectedItem is MessageEntry item
            ? $"{item.When:yyyy-MM-dd HH:mm:ss zzz} | {item.LevelLabel} | {item.Reason}{Environment.NewLine}{Environment.NewLine}{item.What}" : "";
        Grid.SetRow(list, 1); root.Children.Add(list);
        details.IsReadOnly = true;
        details.TextWrapping = TextWrapping.Wrap;
        details.AcceptsReturn = true;
        details.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        details.Margin = new Thickness(0, 10, 0, 10);
        Grid.SetRow(details, 2); root.Children.Add(details);
        var footer = new DockPanel();
        DockPanel.SetDock(older, Dock.Right);
        older.Click += async (_, _) => await ReadMoreAsync();
        footer.Children.Add(older); footer.Children.Add(status);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        history.Added += Added;
        history.Cleared += Refresh;
        Loaded += async (_, _) => await ReadMoreAsync();
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            history.Added -= Added;
            history.Cleared -= Refresh;
        };
    }

    internal async Task ReadMoreAsync()
    {
        if (closed || reading || before == 0)
            return;
        var version = generation;
        reading = true;
        older.IsEnabled = false;
        try
        {
            var page = await history.ReadAsync(before, 200, lifetime.Token);
            if (closed || version != generation)
                return;
            foreach (var entry in page.Entries)
                if (seen.Add(entry.Id))
                    entries.Add(entry);
            if (page.ReadError is null)
                before = page.Before;
            unreadable += page.UnreadableEntries;
            readError = page.ReadError is null ? null : UiText.Format("ErrorDetails", page.ReadError);
            UpdateStatus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ReportFailure(ex); }
        finally
        {
            reading = false;
            older.IsEnabled = !closed && before != 0;
            if (!closed && version != generation)
                await ReadMoreAsync();
        }
    }

    private void Added(MessageEntry entry)
    {
        var version = Volatile.Read(ref generation);
        Dispatcher.BeginInvoke(() =>
        {
            if (!closed && version == generation && seen.Add(entry.Id))
            {
                entries.Insert(0, entry);
                UpdateStatus();
            }
        });
    }

    private void Refresh()
    {
        var version = Interlocked.Increment(ref generation);
        void ClearView()
        {
            if (closed || version != generation)
                return;
            entries.Clear(); seen.Clear(); before = null; unreadable = 0; readError = null;
            _ = ReadMoreAsync();
        }
        if (!Dispatcher.CheckAccess())
            Dispatcher.BeginInvoke(ClearView);
        else
            ClearView();
    }

    private void UpdateStatus() => status.Text = UiText.Format("MessageHistoryCount", entries.Count) +
        (unreadable == 0 ? "" : Environment.NewLine + UiText.Format("MessageHistoryUnreadable", unreadable)) +
        (readError is null ? "" : Environment.NewLine + readError) +
        (history.WriteError is null ? "" : Environment.NewLine + UiText.Format("MessageHistoryWriteFailed", history.WriteError));

    private void ReportFailure(Exception error)
    {
        readError = UiText.Format("ErrorDetails", MessageHistoryStore.Redact(error.Message));
        history.Add(readError, Title, MessageSeverity.Error);
        UpdateStatus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageDialog.ShowMessage(this, UiText.Get("MessageHistoryClearConfirm"), Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            history.Clear();
            MessageJournal.Record(this, UiText.Get("MessageHistoryCleared"), Title);
        }
        catch (Exception ex) { ReportFailure(ex); }
    }
}
