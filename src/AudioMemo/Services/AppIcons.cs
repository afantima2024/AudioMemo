using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioMemo.Services;

public static class AppIcons
{
    internal const int RecordingFrameCount = 32;
    internal const int RainbowRecordingFrameCount = RecordingFrameCount * 6;
    internal static readonly TimeSpan RecordingFrameInterval = TimeSpan.FromMilliseconds(75);
    public static ImageSource Logo { get; } = Load("AppIcon.png");
    public static ImageSource Artwork { get; } = Load("BrandLogo.png");
    public static ImageSource Recording { get; } = WithHeart(Colors.Crimson);
    public static ImageSource Paused { get; } = WithHeart(Colors.DarkOrange);
    private static readonly ImageSource[] recordingFrames = CreateRecordingFrames(rainbow: false);
    private static readonly ImageSource[] rainbowRecordingFrames = CreateRecordingFrames(rainbow: true);

    internal static ImageSource RecordingAt(TimeSpan elapsed, bool rainbow = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        var frames = rainbow ? rainbowRecordingFrames : recordingFrames;
        var frame = (int)(elapsed.Ticks / RecordingFrameInterval.Ticks % frames.Length);
        return frames[frame];
    }

    private static ImageSource[] CreateRecordingFrames(bool rainbow)
    {
        Color[] spectrum = [Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta];
        var count = rainbow ? RainbowRecordingFrameCount : RecordingFrameCount;
        return Enumerable.Range(0, count).Select(frame =>
        {
            if (!rainbow && frame == 0)
                return Recording;
            var phase = frame % RecordingFrameCount;
            var color = Colors.Crimson;
            if (rainbow)
            {
                var index = frame / RecordingFrameCount;
                var from = spectrum[index];
                var to = spectrum[(index + 1) % spectrum.Length];
                byte Blend(byte start, byte end) =>
                    (byte)Math.Round(start + (end - start) * (double)phase / RecordingFrameCount);
                color = Color.FromRgb(Blend(from.R, to.R), Blend(from.G, to.G), Blend(from.B, to.B));
            }
            static double Pulse(int distance) =>
                distance >= 6 ? 0 : (1 + Math.Cos(Math.PI * distance / 6)) / 2;
            var strength = Math.Max(Pulse(Math.Min(phase, RecordingFrameCount - phase)),
                0.85 * Pulse(Math.Abs(phase - RecordingFrameCount / 4)));
            return WithHeart(Color.FromArgb((byte)Math.Round(255 * strength), color.R, color.G, color.B), strength);
        }).ToArray();
    }

    private static ImageSource Load(string name)
    {
        var image = BitmapFrame.Create(new Uri($"pack://application:,,,/AudioMemo;component/Assets/{name}"),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }

    private static ImageSource WithHeart(Color color, double strength = 1)
    {
        var heart = new StreamGeometry();
        using (var path = heart.Open())
        {
            path.BeginFigure(new Point(20, 40), isFilled: true, isClosed: true);
            path.BezierTo(new Point(18, 36), new Point(0, 25), new Point(0, 12), true, true);
            path.BezierTo(new Point(0, 5), new Point(4, 0), new Point(10, 0), true, true);
            path.BezierTo(new Point(15, 0), new Point(18, 3), new Point(20, 7), true, true);
            path.BezierTo(new Point(22, 3), new Point(25, 0), new Point(30, 0), true, true);
            path.BezierTo(new Point(36, 0), new Point(40, 5), new Point(40, 12), true, true);
            path.BezierTo(new Point(40, 25), new Point(22, 36), new Point(20, 40), true, true);
        }
        var scale = 0.8 + 0.2 * strength;
        heart.Transform = new MatrixTransform(scale, 0, 0, scale,
            32 - 20 * scale, 34 - 2 * strength - 20 * scale);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new ImageDrawing(Logo, new Rect(0, 0, 64, 64)));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, heart));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
