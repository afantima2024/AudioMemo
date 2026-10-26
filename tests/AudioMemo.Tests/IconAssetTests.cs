using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioMemo.Tests;

public sealed class IconAssetTests
{
    private static string Assets => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        @"..\..\..\..\..\src\AudioMemo\Assets"));

    [Fact]
    public void GeneratedPngsUseVisibleArtworkInsteadOfTheOriginalTransparentMargins()
    {
        using var source = File.OpenRead(Path.Combine(Assets, "BrandLogo.png"));
        var original = BitmapFrame.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(1024, original.PixelWidth);
        var originalBounds = VisibleBounds(original);
        Assert.Equal(new Int32Rect(123, 173, 778, 648), originalBounds);
        foreach (var (name, size) in new[]
        {
            ("AppIcon.png", 256), ("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150), ("StoreLogo.png", 50)
        })
        {
            using var stream = File.OpenRead(Path.Combine(Assets, name));
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(size, image.PixelWidth);
            AssertLargeArtwork(image);
            if (name == "AppIcon.png")
            {
                var bounds = VisibleBounds(image);
                Assert.InRange(bounds.Width, 250, 252);
                Assert.InRange(bounds.Height, 208, 210);
            }
        }
    }

    [Fact]
    public void NativeIconHasSharpTransparentFramesForNormalAndHighDpiTaskbars()
    {
        var frames = ReadIconFrames();
        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 96, 128, 256], frames.Select(frame => frame.Size));
        foreach (var (size, png) in frames)
        {
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8));
            using var stream = new MemoryStream(png);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(size, image.PixelWidth);
            AssertLargeArtwork(image);
        }
    }

    internal static List<(int Size, byte[] Png)> ReadIconFrames()
    {
        using var stream = File.OpenRead(Path.Combine(Assets, "AudioMemo.ico"));
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        var frames = new List<(int Size, byte[] Png)>();
        for (var index = 0; index < count; index++)
        {
            stream.Position = 6 + index * 16;
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            Assert.Equal(width, height);
            Assert.Equal(0, reader.ReadUInt16());
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            var length = reader.ReadInt32();
            var offset = reader.ReadUInt32();
            Assert.InRange(offset, (uint)(6 + count * 16), (uint)(stream.Length - length));
            stream.Position = offset;
            var png = reader.ReadBytes(length);
            Assert.Equal(length, png.Length);
            frames.Add((width == 0 ? 256 : width, png));
        }
        return frames;
    }

    internal static byte[] ExtractWindowsIcon(string path, int size)
    {
        var icons = new nint[1];
        var identifiers = new uint[1];
        var count = PrivateExtractIcons(path, 0, size, size, icons, identifiers, 1, 0);
        try
        {
            Assert.Equal(1u, count);
            Assert.NotEqual(nint.Zero, icons[0]);
            var image = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Assert.Equal(size, image.PixelWidth);
            Assert.Equal(size, image.PixelHeight);
            var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            var pixels = new byte[size * size * 4];
            converted.CopyPixels(pixels, size * 4, 0);
            return pixels;
        }
        finally { if (icons[0] != nint.Zero) Assert.True(DestroyIcon(icons[0])); }
    }

    internal static byte[] ExtractSourceWindowsIcon(int size) => ExtractWindowsIcon(Path.Combine(Assets, "AudioMemo.ico"), size);

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string path, int index, int width, int height,
        [Out] nint[] icons, [Out] uint[] identifiers, uint count, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    internal static void AssertLargeArtwork(BitmapSource image)
    {
        Assert.Equal(image.PixelWidth, image.PixelHeight);
        var bounds = VisibleBounds(image);
        var size = image.PixelWidth;
        Assert.InRange((double)bounds.Width / size, size < 32 ? 0.90 : 0.95, 1.0);
        Assert.InRange((double)bounds.Height / size, 0.75, 0.88);
        Assert.InRange((double)bounds.Width / bounds.Height, 1.10, 1.30);
        Assert.InRange(Math.Abs(bounds.X + bounds.Width / 2.0 - size / 2.0), 0, 1);
        Assert.InRange(Math.Abs(bounds.Y + bounds.Height / 2.0 - size / 2.0), 0, 1);
        Assert.True(bounds.X >= 0 && bounds.Y > 0 && bounds.X + bounds.Width <= size && bounds.Y + bounds.Height < size);
    }

    private static Int32Rect VisibleBounds(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var left = image.PixelWidth;
        var top = image.PixelHeight;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < image.PixelHeight; y++)
            for (var x = 0; x < image.PixelWidth; x++)
                if (pixels[y * stride + x * 4 + 3] >= 8)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
        Assert.True(right >= left && bottom >= top);
        return new(left, top, right - left + 1, bottom - top + 1);
    }
}
