using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyUnlabeledSpeakerColors()
    {
        var view = new TranscriptView { CanSetSpeakerColors = true, CanChangeSpeakers = false };
        var root = new Grid();
        root.Children.Add(view);
        root.Measure(new Size(1000, 550));
        root.Arrange(new Rect(0, 0, 1000, 550));
        var grid = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
        var document = new TranscriptDocument
        {
            Revision = "synthetic",
            Segments =
            [
                new(0, 1, "Known voice.") { SpeakerId = "spkr1" },
                new(2, 3, "First unlabeled voice."),
                new(4, 5, "Known voice again.") { SpeakerId = "spkr1" },
                new(6, 7, "Another unlabeled voice.")
            ]
        };
        document.SetSpeakerColor("spkr1", "#F2D2D2");
        document.SetSpeakerColor(null, "#294B63");
        view.ConfigureShortcuts(new());
        view.SetDocument(document, reset: true);
        Pump(root);
        var requests = new List<string?>();
        view.SpeakerColorRequested += requests.Add;
        var color = Assert.IsType<MenuItem>(view.FindName("SpeakerColorMenu"));
        grid.SelectedIndex = 1;
        Pump(root);
        Assert.True(color.IsEnabled);
        Assert.Equal(UiText.Get("SetUnlabeledSpeakerColor"), color.Header);
        Assert.Contains(UiText.Get("SpeakerColorHint"), Assert.IsType<string>(color.ToolTip));
        Assert.False(Assert.IsType<MenuItem>(view.FindName("RenameSpeakerMenu")).IsEnabled);
        Assert.False(Assert.IsType<MenuItem>(view.FindName("CorrectSpeakerMenu")).IsEnabled);
        Click(color);
        Assert.True(ShortcutActions.Invoke(view.ShortcutTarget("speakerColor")));
        var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.PreviewMouseDoubleClickEvent,
            Source = grid.Columns[1].GetCellContent(grid.Items[1])
        };
        typeof(TranscriptView).GetMethod("Grid_PreviewMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [grid, mouse]);
        Assert.True(mouse.Handled);
        Assert.Equal(new string?[] { null, null, null }, requests);
        var selected = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(1));
        var other = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(3));
        var expected = AppAppearance.Brush(RgbColor.Parse("#294B63")).Color;
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(other.Background).Color);
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(selected.Resources["SpeakerSwatchBrush"]).Color);
        Assert.Equal(Assert.IsType<SolidColorBrush>(view.SelectionBackground).Color,
            Assert.IsType<SolidColorBrush>(selected.Background).Color);
        AssertReadable(Assert.IsType<SolidColorBrush>(other.Foreground).Color, expected);
        var unchangedRow = grid.Items[3];
        var neutral = document.Copy();
        neutral.SetSpeakerColor(null, null);
        view.SetDocument(neutral);
        Pump(root);
        Assert.Same(unchangedRow, grid.Items[3]);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(other.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(selected.Resources["SpeakerSwatchBrush"]).Color);
        Assert.Equal(Assert.IsType<SolidColorBrush>(view.SelectionBackground).Color,
            Assert.IsType<SolidColorBrush>(selected.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(
            SpeakerColors.ForSpeaker(null, document.SpeakerColors, true, document.UnlabeledSpeakerColor)).Color);
        view.CanSetSpeakerColors = false;
        Assert.False(color.IsEnabled);
        Click(color);
        Assert.False(ShortcutActions.Invoke(view.ShortcutTarget("speakerColor")));
        Assert.Equal(3, requests.Count);
        view.CanSetSpeakerColors = true;
        var unlabeledOnly = new TranscriptDocument { Segments = [new(0, 1, "Only unlabeled speech.")] };
        view.SetDocument(unlabeledOnly, reset: true);
        grid.SelectedIndex = 0;
        Pump(root);
        Assert.True(color.IsEnabled);
        Assert.Equal(UiText.Get("UnlabeledSpeakers"), Assert.IsType<TranscriptRow>(grid.Items[0]).DisplaySpeaker);
        Click(color);
        Assert.Equal(4, requests.Count);
        Assert.Null(requests[^1]);
        root.Children.Clear();
    }
}
