using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace AudioMemo.Services;

internal static class RecycleBin
{
    public static void Move(Func<string> validatedPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { MoveOnStaThread(validatedPath()); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void MoveOnStaThread(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType != DriveType.Fixed)
            throw new IOException("Recycle Bin deletion requires a local fixed drive. The session was not removed.");
        var id = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out var item));
        var operation = (IFileOperation)new FileOperation();
        try
        {
            // RECYCLEONDELETE, EARLYFAILURE, NOERRORUI, SILENT, NOCONFIRMATION, ADDUNDORECORD.
            // Unlike ALLOWUNDO, RECYCLEONDELETE explicitly requests recycling, not permanent deletion.
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x00000400 | 0x00000004 | 0x00000010 | 0x20000000);
            operation.DeleteItem(item, IntPtr.Zero);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted)
                throw new OperationCanceledException("The Recycle Bin operation was cancelled.");
            if (Directory.Exists(path))
                throw new IOException("The session was not moved to the Recycle Bin.");
        }
        finally
        {
            Marshal.FinalReleaseComObject(operation);
            Marshal.FinalReleaseComObject(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem;

    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    private class FileOperation;

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
