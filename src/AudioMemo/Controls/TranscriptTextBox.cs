using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Controls;

public sealed class TranscriptTextBox : TextBox
{
    public static readonly DependencyProperty MatchBrushProperty = DependencyProperty.Register(
        nameof(MatchBrush), typeof(Brush), typeof(TranscriptTextBox),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ActiveMatchBrushProperty = DependencyProperty.Register(
        nameof(ActiveMatchBrush), typeof(Brush), typeof(TranscriptTextBox),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private IReadOnlyList<TranscriptMatch> matches = [];
    private TranscriptMatch? active;
    public Brush MatchBrush { get => (Brush)GetValue(MatchBrushProperty); set => SetValue(MatchBrushProperty, value); }
    public Brush ActiveMatchBrush { get => (Brush)GetValue(ActiveMatchBrushProperty); set => SetValue(ActiveMatchBrushProperty, value); }
    public IReadOnlyList<TranscriptMatch> Matches => matches;
    public TranscriptMatch? ActiveMatch => active;
    internal bool IsUpdatingContent { get; private set; }
    internal event EventHandler? ContentUpdated;

    public TranscriptTextBox()
    {
        SetResourceReference(MatchBrushProperty, "SearchHighlight");
        SetResourceReference(ActiveMatchBrushProperty, "SearchActiveHighlight");
        SizeChanged += (_, _) => InvalidateVisual();
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
    }

    public void SetMatches(IReadOnlyList<TranscriptMatch> next, TranscriptMatch? selected)
    {
        matches = next;
        active = selected;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        var contentChanged = e.Property == TextProperty || e.Property == DataContextProperty;
        var previous = IsUpdatingContent;
        IsUpdatingContent |= contentChanged;
        try { base.OnPropertyChanged(e); }
        finally { IsUpdatingContent = previous; }
        if (contentChanged && !previous)
            ContentUpdated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (matches.Count == 0)
            return;
        var foreground = AppAppearance.Rgb(((SolidColorBrush)Foreground).Color);
        var matchBrush = MatchBrush is SolidColorBrush regular
            ? AppAppearance.Brush(AppAppearance.Rgb(regular.Color).ForText(foreground)) : MatchBrush;
        var activeBrush = ActiveMatchBrush is SolidColorBrush current
            ? AppAppearance.Brush(AppAppearance.Rgb(current.Color).ForText(foreground)) : ActiveMatchBrush;
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        foreach (var match in matches)
        {
            foreach (var rectangle in MatchRectangles(match))
            {
                if (SystemParameters.HighContrast)
                    drawingContext.DrawRectangle(null, new Pen(SystemColors.HighlightBrush, match == active ? 2 : 1), rectangle);
                else
                    drawingContext.DrawRoundedRectangle(match == active ? activeBrush : matchBrush, null, rectangle, 2, 2);
            }
        }
        drawingContext.Pop();
    }

    public IReadOnlyList<Rect> MatchRectangles(TranscriptMatch match)
    {
        var result = new List<Rect>();
        if (!IsArrangeValid || match.Start < 0 || match.End > Text.Length)
            return result;
        for (var position = match.Start; position < match.End;)
        {
            var start = GetRectFromCharacterIndex(position);
            var line = GetLineIndexFromCharacterIndex(position);
            if (start.IsEmpty || line < 0)
                break;
            var lineEnd = GetCharacterIndexFromLineIndex(line) + GetLineLength(line);
            var end = Math.Min(match.End, Math.Max(position + 1, lineEnd));
            var last = end - 1;
            while (last > position && Text[last] is '\r' or '\n')
                last--;
            var finish = GetRectFromCharacterIndex(last, true);
            if (!finish.IsEmpty && finish.Top == start.Top)
                result.Add(new Rect(start.Left, start.Top, Math.Max(2, finish.Right - start.Left), start.Height));
            position = end;
        }
        return result;
    }
}
