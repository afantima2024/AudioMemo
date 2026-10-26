using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class ExplorerSelectionTests
{
    [Fact]
    public void ExplorerReceivesRelativeChildIdsForExactlyTheSelectedFolders()
    {
        using var folder = new TestFolder();
        var expected = new[] { "Meeting one", "\u4f1a\u8bae two" }.Select(name =>
            Directory.CreateDirectory(folder.File(name)).FullName).ToArray();
        Exception? failure = null;
        var calls = 0;
        var thread = new Thread(() =>
        {
            try
            {
                ExplorerSelection.Open(expected, (parent, children) =>
                {
                    calls++;
                    Assert.Equal(folder.Root, PathFor(parent));
                    var selected = children.Select(child =>
                    {
                        var full = ILCombine(parent, child);
                        Assert.NotEqual(0, full);
                        try { return PathFor(full); }
                        finally { Marshal.FreeCoTaskMem(full); }
                    }).ToArray();
                    Assert.Equal(expected, selected);
                });
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(1, calls);
    }

    private static string PathFor(nint pidl)
    {
        var path = new StringBuilder(32768);
        Assert.True(SHGetPathFromIDListEx(pidl, path, checked((uint)path.Capacity), 0));
        return path.ToString();
    }

    [DllImport("shell32.dll")]
    private static extern nint ILCombine(nint parent, nint child);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(nint pidl, StringBuilder path, uint capacity, uint flags);
}
