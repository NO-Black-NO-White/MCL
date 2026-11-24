using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MCL.Services;

public sealed class WindowsVersionRecycleBin : IVersionRecycleBin
{
    public Task MoveDirectoryAsync(string absolutePath, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { ct.ThrowIfCancellationRequested(); Recycle(absolutePath, ct); completion.TrySetResult(); }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "MCL version recycle" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static void Recycle(string absolutePath, CancellationToken ct)
    {
        // The management service has checked this named, absolute versions/<id> target.
        if (!Path.IsPathFullyQualified(absolutePath) || !string.Equals(Path.GetFullPath(absolutePath), absolutePath, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(absolutePath) is null || !Directory.Exists(absolutePath)) throw new IOException("待删除的版本路径无效。");
        if ((File.GetAttributes(absolutePath) & FileAttributes.ReparsePoint) != 0) throw new IOException("待删除版本已变为链接，操作已停止。");
        VerifyRecycleSupport(absolutePath, ct);
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            // RECYCLEONDELETE explicitly requests recycling; there is no permanent-delete fallback.
            // ADDUNDORECORD also makes the user-initiated action available to Explorer's undo stack.
            operation.SetOperationFlags(0x0004 | 0x0010 | 0x0040 | 0x0400 | 0x4000 | 0x00080000 | 0x00100000 | 0x20000000);
            var itemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(absolutePath, IntPtr.Zero, ref itemId, out item));
            operation.DeleteItem(item, IntPtr.Zero);
            ct.ThrowIfCancellationRequested();
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted) throw new OperationCanceledException("移入回收站的操作已取消。");
        }
        catch (COMException ex)
        { throw new IOException("无法将版本移入 Windows 回收站。请检查文件是否被占用，以及该磁盘是否支持回收站。", ex); }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
        }
    }

    private static void VerifyRecycleSupport(string path, CancellationToken ct)
    {
        var driveRoot = Path.GetPathRoot(path)!;
        if (new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
            throw new IOException("这个位置无法保证使用 Windows 回收站，MCL 已停止删除。请在资源管理器中处理该版本。");
        using var userPolicy = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
        using var machinePolicy = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
        using var global = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket");
        var volumeName = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPoint(driveRoot, volumeName, (uint)volumeName.Capacity)) throw new IOException("无法确认磁盘的回收站设置，MCL 已停止删除。");
        var volumeText = volumeName.ToString();
        var begin = volumeText.IndexOf('{'); var end = volumeText.IndexOf('}');
        if (begin < 0 || end < begin) throw new IOException("无法确认磁盘的回收站设置，MCL 已停止删除。");
        using var volume = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + volumeText[begin..(end + 1)]);
        static bool Enabled(RegistryKey? key, string value) => Convert.ToInt64(key?.GetValue(value, 0) ?? 0) != 0;
        if (Enabled(userPolicy, "NoRecycleFiles") || Enabled(machinePolicy, "NoRecycleFiles") || Enabled(volume, "NukeOnDelete")
            || (Enabled(global, "UseGlobalSettings") && Enabled(global, "NukeOnDelete")))
            throw new IOException("Windows 已禁用此位置的回收站。为避免永久删除，MCL 已停止操作。");
        var capacityMb = volume?.GetValue("MaxCapacity") is { } configured ? Convert.ToInt64(configured) : 0;
        long size = 0;
        var pending = new Stack<string>(); pending.Push(path);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            ct.ThrowIfCancellationRequested();
            if (directory.Length >= 260 || directory.StartsWith("\\\\?\\", StringComparison.Ordinal)) throw new IOException("版本路径过长，MCL 已停止删除。");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("版本路径已变为链接，MCL 已停止删除。");
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.Length >= 260) throw new IOException("版本包含过长路径，MCL 已停止删除。");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("版本中出现链接，MCL 已停止删除。");
                if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Push(entry.FullName);
                else size = checked(size + ((FileInfo)entry).Length);
            }
        }
        if (capacityMb > 0 && size > capacityMb * 1024L * 1024L)
            throw new IOException("该版本超过当前 Windows 回收站的容量。为避免永久删除，MCL 已停止操作。");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder name, uint capacity);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
