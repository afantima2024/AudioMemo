using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using AudioMemo.Core.Localization;

namespace AudioMemo.Services;

internal static class WindowSizing
{
    private static bool initialized;
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(WindowSizing), new PropertyMetadata(false));

    public static void Initialize()
    {
        if (initialized)
            return;
        initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Attach((Window)sender)));
    }

    private static void Attach(Window window)
    {
        if ((bool)window.GetValue(AttachedProperty) || PresentationSource.FromVisual(window) is not HwndSource source)
            return;
        window.SetValue(AttachedProperty, true);
        var queued = false;
        var failed = false;
        var minimum = new Size(window.MinWidth, window.MinHeight);
        var maximum = new Size(window.MaxWidth, window.MaxHeight);
        void UpdateBounds()
        {
            if (failed || source.IsDisposed)
                return;
            try { Fit(window, source.Handle, minimum, maximum); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                failed = true;
                MessageDialog.ShowMessage(window, UiText.Format("DisplaySizingFailed", ex.Message), "AudioMemo",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x0024 && WindowChrome.GetWindowChrome(window) is not null && !failed)
            {
                try
                {
                    var monitor = ReadMonitor(hwnd);
                    var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    SetMaximizedBounds(ref bounds, monitor.Monitor, monitor.Work, GetDpiForWindow(hwnd), minimum, maximum);
                    Marshal.StructureToPtr(bounds, lParam, false);
                    handled = true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    failed = true;
                    window.Dispatcher.BeginInvoke(() => MessageDialog.ShowMessage(window,
                        UiText.Format("DisplaySizingFailed", ex.Message), "AudioMemo", MessageBoxButton.OK, MessageBoxImage.Warning));
                }
                return 0;
            }
            if (message is 0x02E0 or 0x0047 or 0x001A && !queued && !failed)
            {
                queued = true;
                window.Dispatcher.BeginInvoke(() =>
                {
                    queued = false;
                    UpdateBounds();
                }, DispatcherPriority.Loaded);
            }
            return 0;
        }
        source.AddHook(Hook);
        window.Closed += (_, _) =>
        {
            if (!source.IsDisposed)
                source.RemoveHook(Hook);
        };
        UpdateBounds();
    }

    private static void Fit(Window window, nint handle, Size minimum, Size maximum)
    {
        var info = ReadMonitor(handle);
        var dpi = GetDpiForWindow(handle);
        FitToArea(window, AvailableSize(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, dpi), minimum, maximum);
    }

    private static MonitorInfo ReadMonitor(nint handle)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }

    internal static void SetMaximizedBounds(ref MinMaxInfo bounds, NativeRect monitor, NativeRect work,
        uint dpi, Size minimum, Size maximum)
    {
        var area = AvailableSize(work.Right - work.Left, work.Bottom - work.Top, dpi);
        var width = Math.Min(area.Width, maximum.Width);
        var height = Math.Min(area.Height, maximum.Height);
        int Pixels(double value) => checked((int)Math.Round(value * dpi / 96d));
        bounds.MaxPosition = new() { X = work.Left - monitor.Left, Y = work.Top - monitor.Top };
        bounds.MaxSize = new() { X = Pixels(width), Y = Pixels(height) };
        bounds.MaxTrackSize = bounds.MaxSize;
        bounds.MinTrackSize = new()
        {
            X = Pixels(Math.Min(minimum.Width, width)),
            Y = Pixels(Math.Min(minimum.Height, height))
        };
    }

    internal static Size AvailableSize(int pixelWidth, int pixelHeight, uint dpi)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || dpi == 0)
            throw new InvalidOperationException("Windows did not provide valid display dimensions or DPI.");
        return new Size(pixelWidth * 96d / dpi, pixelHeight * 96d / dpi);
    }

    internal static void FitToArea(Window window, Size area, Size minimum, Size maximum)
    {
        var width = Math.Min(area.Width, maximum.Width);
        var height = Math.Min(area.Height, maximum.Height);
        // Custom chrome uses exact native work-area bounds, not the oversized standard resize frame.
        var customChrome = WindowChrome.GetWindowChrome(window) is not null;
        window.MaxWidth = customChrome ? maximum.Width : width;
        window.MaxHeight = customChrome ? maximum.Height : height;
        window.MinWidth = Math.Min(minimum.Width, width);
        window.MinHeight = Math.Min(minimum.Height, height);
        if (window.WindowState == WindowState.Normal)
        {
            if (double.IsFinite(window.Width))
                window.Width = Math.Clamp(window.Width, window.MinWidth, width);
            if (double.IsFinite(window.Height))
                window.Height = Math.Clamp(window.Height, window.MinHeight, height);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
