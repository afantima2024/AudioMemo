using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AudioMemo.Core.Models;

namespace AudioMemo.Controls;

public partial class TranscriptView
{
    private TextSelectionSnapshot? textSelection;
    private bool synchronizingTextSelection;

    private void InitializeTextSelection()
    {
        RowsGrid.AddHandler(Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(TextSelection_PreviewMouseDown), handledEventsToo: true);
        RowsGrid.AddHandler(CommandManager.PreviewCanExecuteEvent, new CanExecuteRoutedEventHandler((_, e) =>
        {
            if (e.Command == ApplicationCommands.Copy && GetSelectedText() is not null)
            {
                e.CanExecute = true;
                e.Handled = true;
            }
        }));
        RowsGrid.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler((_, e) =>
        {
            if (e.Command == ApplicationCommands.Copy && GetSelectedText() is { } selectedText)
            {
                Copy(selectedText);
                e.Handled = true;
            }
        }));
    }

    private string? GetSelectedText() => textSelection is { } selection
        ? selection.Row.Text.Substring(selection.Start, selection.Length) : null;

    private void TextSelection_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right))
            return;
        var source = e.OriginalSource as DependencyObject;
        TranscriptTextBox? textBox = null;
        for (var element = source; element is not null && element != RowsGrid;
            element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element))
        {
            if (element is ScrollBar)
                return;
            if (element is TranscriptTextBox box)
                textBox = box;
        }
        if (textBox?.DataContext is not TranscriptRow row || !ReferenceEquals(textSelection?.Row, row))
            ClearTextSelection();
    }

    private void Text_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TranscriptTextBox box || synchronizingTextSelection || box.IsUpdatingContent ||
            box.DataContext is not TranscriptRow row || box.Text != row.Text)
            return;
        if (box.SelectionLength > 0)
        {
            textSelection = new(row, box.SelectionStart, box.SelectionLength);
            SynchronizeTextSelection(() =>
            {
                foreach (var other in VisualChildren<TranscriptTextBox>(RowsGrid))
                    if (!ReferenceEquals(other, box) && other.SelectionLength > 0)
                        other.Select(other.SelectionStart, 0);
            });
            IsFollowing = false;
            UpdateFollowLabel();
        }
        else if (ReferenceEquals(textSelection?.Row, row))
        {
            textSelection = null;
            if (pending is not null)
                SetDocument(pending);
        }
    }

    private void ClearTextSelection()
    {
        textSelection = null;
        SynchronizeTextSelection(() =>
        {
            foreach (var box in VisualChildren<TranscriptTextBox>(RowsGrid))
                if (box.SelectionLength > 0)
                    box.Select(box.SelectionStart, 0);
        });
    }

    private void ReconcileTextSelection(IReadOnlyList<TranscriptRow> nextRows, bool reset)
    {
        if (textSelection is not { } selection)
            return;
        if (!reset && nextRows.Any(row => ReferenceEquals(row, selection.Row)))
            return;
        if (!reset)
        {
            var prefix = selection.Row.Text[..(selection.Start + selection.Length)];
            var candidates = nextRows.Where(row => row.StartSeconds == selection.Row.StartSeconds &&
                row.SpeakerId == selection.Row.SpeakerId && row.Text.StartsWith(prefix, StringComparison.Ordinal)).Take(2).ToArray();
            if (candidates.Length == 1)
            {
                textSelection = selection with { Row = candidates[0] };
                return;
            }
        }
        ClearTextSelection();
    }

    private void TextCell_ContentUpdated(object? sender, EventArgs e) => RestoreTextSelection((TranscriptTextBox)sender!);

    private void RestoreTextSelection(TranscriptTextBox box)
    {
        var selection = textSelection;
        var restore = selection is not null && ReferenceEquals(box.DataContext, selection.Row) && box.Text == selection.Row.Text;
        var start = restore ? selection!.Start : 0;
        var length = restore ? selection!.Length : 0;
        if (box.SelectionLength != length || (restore && box.SelectionStart != start))
            SynchronizeTextSelection(() => box.Select(start, length));
    }

    private void SynchronizeTextSelection(Action action)
    {
        var previous = synchronizingTextSelection;
        synchronizingTextSelection = true;
        try { action(); }
        finally { synchronizingTextSelection = previous; }
    }

    private sealed record TextSelectionSnapshot(TranscriptRow Row, int Start, int Length);
}
