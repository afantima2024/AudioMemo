using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioMemo.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyComparisonColors(Application application, string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = new AppSettings { UiLanguage = language, SaveFolder = folder.Root };
        var window = new TranscriptEditWindow(session, TranscriptCorrectionTests.Plan(session, 0, 1, 2),
            settings, false, (_, _) => { });
        try
        {
            var content = Assert.IsType<Grid>(window.Content);
            // VisualBrush sampling needs a zero-offset root around the dialog's outer margin.
            var root = new Grid();
            window.Content = root;
            root.Children.Add(content);
            var grid = Assert.IsType<DataGrid>(window.FindName("CorrectionRows"));
            var rows = grid.Items.Cast<TranscriptEditWindow.CorrectionRow>().ToArray();
            var baseline = rows.Select(row => row.LocalText).ToArray();
            Call(window, "ApplyRecognitionRow", new SelectedTranscriptionRow(0, baseline[0], false));
            Call(window, "ApplyRecognitionRow", new SelectedTranscriptionRow(1, "A different local result.", false));
            Call(window, "ApplyRecognitionRow", new SelectedTranscriptionRow(2, "Unused fallback.", true));
            rows[0].OnlineText = rows[0].LocalText;
            rows[1].OnlineText = "A different synthetic result.";
            rows[2].OnlineText = "";
            Assert.Equal(baseline, rows.Select(row => row.LocalText));
            Assert.Equal(baseline[2], rows[2].Text);
            Assert.Equal(language == "zh-CN" ? "修改后的文字" : "Corrected text", grid.Columns[3].Header);
            foreach (var online in new[] { false, true })
            {
                if (online)
                    Call(window, "ToggleComparison");
                Assert.Equal(Visibility.Visible, Assert.IsType<WrapPanel>(window.FindName("ComparisonActions")).Visibility);
                Assert.Equal(online ? Visibility.Visible : Visibility.Collapsed,
                    Assert.IsType<Button>(window.FindName("UseOnlineResultsButton")).Visibility);
                Assert.Equal(online ? Visibility.Visible : Visibility.Collapsed,
                    Assert.IsType<CheckBox>(window.FindName("OnlyDifferencesCheck")).Visibility);
                foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
                    foreach (var custom in new[] { false, true })
                    {
                        settings.Theme = theme;
                        settings.Appearance = new();
                        var palette = theme == AppThemes.Dark ? settings.Appearance.DarkComparison : settings.Appearance.LightComparison;
                        if (custom)
                        {
                            palette.Matching = "#010101";
                            palette.Different = "#FDFDFD";
                            palette.Unclear = "#97617C";
                        }
                        AppAppearance.Apply(application, settings);
                        root.Measure(new Size(1060, 740));
                        root.Arrange(new Rect(0, 0, 1060, 740));
                        Pump(root);
                        grid.UnselectAllCells();
                        Pump(root);
                        var expected = new[] { palette.Matching, palette.Different, palette.Unclear };
                        var pixels = RenderPixels(root);
                        for (var index = 0; index < rows.Length; index++)
                        {
                            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(index));
                            var background = AppAppearance.Brush(RgbColor.Parse(expected[index])).Color;
                            Assert.Equal(background, Assert.IsType<SolidColorBrush>(row.Background).Color);
                            AssertReadable(Assert.IsType<SolidColorBrush>(row.Foreground).Color, background);
                            foreach (var column in grid.Columns.Where(column => column.Visibility == Visibility.Visible))
                            {
                                var text = Assert.IsType<TextBlock>(column.GetCellContent(rows[index]));
                                AssertReadable(Assert.IsType<SolidColorBrush>(text.Foreground).Color, background);
                            }
                            var cell = FindChild<DataGridCell>(row)!;
                            Assert.Equal(background, Assert.IsType<SolidColorBrush>(cell.Background).Color);
                            var chrome = Assert.IsType<Border>(cell.Template.FindName("CellChrome", cell));
                            Assert.Equal(background, Assert.IsType<SolidColorBrush>(chrome.Background).Color);
                            var pixel = Pixel(cell, (int)cell.ActualWidth - 8, 3, pixels);
                            Assert.True(background == pixel,
                                $"{theme}, custom={custom}, row={index}: expected {background}, painted {pixel}; " +
                                $"cell={cell.RenderSize}, point={cell.TranslatePoint(new Point(cell.ActualWidth - 8, 3), root)}, " +
                                $"row={row.RenderSize}, grid={grid.RenderSize}, selected={cell.IsSelected}.");
                        }
                        var unclear = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(2));
                        Assert.Equal(UiText.Get(online ? "ComparisonUnclearHint" : "SelectionRowNoSpeech"), unclear.ToolTip);
                        if (!online)
                        {
                            // Theme changes replace hidden-window cells without a native focus notification.
                            grid.CurrentCell = default;
                            var current = new DataGridCellInfo(rows[0], grid.Columns[3]);
                            grid.SelectedCells.Add(current);
                            grid.CurrentCell = current;
                            Assert.True(grid.BeginEdit(),
                                $"Cannot edit: theme={theme}, custom={custom}, enabled={grid.IsEnabled}, " +
                                $"readOnly={grid.IsReadOnly}/{grid.Columns[3].IsReadOnly}, column={grid.Columns[3].Visibility}.");
                            Pump(root);
                            var editor = Assert.IsType<TextBox>(grid.Columns[3].GetCellContent(rows[0]));
                            foreach (var (text, state, color) in new[]
                            {
                            ("Manual change.", TranscriptComparisonState.Different, palette.Different),
                            (" ", TranscriptComparisonState.Unclear, palette.Unclear),
                            (baseline[0], TranscriptComparisonState.Matching, palette.Matching)
                        })
                            {
                                editor.Text = text;
                                Pump(root);
                                Assert.Equal(text, rows[0].Text);
                                Assert.Equal(state, rows[0].ComparisonState);
                                Assert.False(rows[0].OnlineDifferent);
                                Assert.Equal(baseline[0], Assert.IsType<TextBlock>(grid.Columns[2].GetCellContent(rows[0])).Text);
                                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(rows[0]));
                                var background = AppAppearance.Brush(RgbColor.Parse(color)).Color;
                                Assert.Equal(background, BrushColor(row.Background));
                                var cell = FindChild<DataGridCell>(row)!;
                                Assert.Equal(background, Pixel(cell, (int)cell.ActualWidth - 8, 3));
                            }
                            Assert.True(grid.CommitEdit(DataGridEditingUnit.Cell, true));
                            Assert.True(grid.CommitEdit(DataGridEditingUnit.Row, true));
                        }
                        grid.SelectedCells.Add(new DataGridCellInfo(rows[0], grid.Columns[2]));
                        Pump(root);
                        var selectedText = Assert.IsType<TextBlock>(grid.Columns[2].GetCellContent(rows[0]));
                        AssertReadable(Assert.IsType<SolidColorBrush>(selectedText.Foreground).Color,
                            Assert.IsType<SolidColorBrush>(application.FindResource("TranscriptSelectionBackground")).Color);
                    }
            }
            var differences = Assert.IsType<CheckBox>(window.FindName("OnlyDifferencesCheck"));
            differences.IsChecked = true;
            differences.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([rows[1], rows[2]], grid.Items.Cast<TranscriptEditWindow.CorrectionRow>());
            var saved = OnlineSpeechTests.Comparison(session, TranscriptCorrectionTests.Plan(session, 0, 1, 2));
            saved.Rows[0] = saved.Rows[0] with
            {
                LocalText = "Older unsaved local preview.",
                LocalUnrecognized = true,
                OnlineText = baseline[0]
            };
            saved.Rows[1] = saved.Rows[1] with { LocalUnrecognized = true };
            rows[0].SetCorrection("Retained current draft.", unrecognized: true);
            Call(window, "ShowOnlineResult", saved);
            Assert.Equal(baseline, rows.Select(row => row.LocalText));
            Assert.Equal("Retained current draft.", rows[0].Text);
            Assert.True(rows[0].Unrecognized);
            Assert.Equal(TranscriptComparisonState.Matching, rows[0].ComparisonState);
            Assert.Equal(TranscriptComparisonState.Different, rows[1].ComparisonState);
            Assert.False(rows[1].Unrecognized);
            Assert.Equal([rows[1], rows[2]], grid.Items.Cast<TranscriptEditWindow.CorrectionRow>());
            Call(window, "ToggleComparison");
            Assert.Equal(TranscriptComparisonState.Unclear, rows[0].ComparisonState);
            Assert.Equal(rows, grid.Items.Cast<TranscriptEditWindow.CorrectionRow>());
        }
        finally
        {
            window.Close();
            AppAppearance.Apply(application, AppThemes.Light);
        }
    }
}
