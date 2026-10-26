using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace AudioMemo.Services;

internal sealed record LibraryStatistics(long? OccupiedBytes, long? FreeBytes)
{
    public static LibraryStatistics Read(string root, IReadOnlyList<string> sessionFolders,
        bool occupied, bool free, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long? size = null;
        if (occupied)
        {
            size = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            foreach (var folder in sessionFolders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Recording statistics cannot follow a linked recording folder.");
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    size = checked(size.Value + file.Length);
                }
            }
        }
        long? available = null;
        if (free)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GetDiskFreeSpaceEx(root, out var bytes, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            available = checked((long)bytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(size, available);
    }

    public static string FormatBytes(long bytes, CultureInfo culture)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return value.ToString(index == 0 ? "N0" : "N1", culture) + " " + units[index];
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
