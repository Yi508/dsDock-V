using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DsDock.Diagnostics;

namespace DsDock.Platform;

/// <summary>
/// 现代 Windows 文件夹选择器：IFileOpenDialog + FOS_PICKFOLDERS。
/// 外观与"打开文件"对话框完全一致（导入配置用的就是同一种），并且可以挂到宿主窗口上 ——
/// 有 owner 的对话框天然在上面，不需要靠临时取消置顶来避免被压住。
/// </summary>
internal static class FolderPicker
{
    private const uint FOS_PICKFOLDERS = 0x20;
    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const uint FOS_PATHMUSTEXIST = 0x800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    /// <summary>返回选中的文件夹路径；用户取消或失败返回 null（不抛异常）。</summary>
    public static string? Pick(Window? owner, string title)
    {
        try
        {
            var dialog = (IFileDialog)new FileOpenDialogRCW();
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            dialog.SetTitle(title);

            IntPtr hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
            int hr = dialog.Show(hwnd);
            if (hr == ERROR_CANCELLED) { Log.Info("文件夹选择器: 用户取消"); return null; }
            if (hr != 0) { Log.Info($"文件夹选择器: Show 返回 0x{hr:X}"); return null; }

            dialog.GetResult(out IShellItem item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr buffer);
            string path = Marshal.PtrToStringUni(buffer) ?? "";
            Marshal.FreeCoTaskMem(buffer);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception ex)
        {
            Log.Info("文件夹选择器失败: " + ex.GetType().Name + " — " + ex.Message);
            return null;
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW
    {
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
