using System.IO;
using System.Runtime.InteropServices;
using AudioMemo.Core.Localization;

namespace AudioMemo.Services;

internal static class ExplorerSelection
{
    internal static (string Parent, string[] Folders) Prepare(IReadOnlyList<string> folders)
    {
        var paths = folders.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
            throw new InvalidOperationException(UiText.Get("SelectSession"));
        var parent = Path.GetDirectoryName(paths[0]) ?? throw new IOException(UiText.Get("SelectedFoldersNeedSameParent"));
        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase))
                throw new IOException(UiText.Get("SelectedFoldersNeedSameParent"));
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException(path);
        }
        return (parent, paths);
    }

    public static void Open(IReadOnlyList<string> folders, Action<nint, nint[]>? select = null)
    {
        var selection = Prepare(folders);
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        var allocated = new List<nint>();
        try
        {
            var parent = Parse(selection.Parent);
            var children = selection.Folders.Select(path => ILFindLastID(Parse(path))).ToArray();
            if (children.Any(pointer => pointer == 0))
                throw new IOException(UiText.Get("SelectedFoldersNeedSameParent"));
            if (select is null)
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(parent, checked((uint)children.Length), children, 0));
            else
                select(parent, children);
        }
        finally
        {
            foreach (var pointer in allocated)
                Marshal.FreeCoTaskMem(pointer);
            CoUninitialize();
        }

        nint Parse(string path)
        {
            var result = SHParseDisplayName(path, 0, out var pointer, 0, out _);
            if (pointer != 0)
                allocated.Add(pointer);
            Marshal.ThrowExceptionForHR(result);
            if (pointer == 0)
                throw new IOException(UiText.Get("SelectedFoldersNeedSameParent"));
            return pointer;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint mode);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindingContext, out nint pidl, uint attributes, out uint resultAttributes);

    [DllImport("shell32.dll")]
    private static extern nint ILFindLastID(nint pidl);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint parent, uint count,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] nint[] children, uint flags);
}
