using System.Runtime.InteropServices;

namespace LanFileShare.Services;

// Windows Shell 文件夹选择对话框（比 WinForms FolderBrowserDialog 好用）
public static class ModernFolderDialog
{
    // CLSID for FileOpenDialog
    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    // CLSID for FileSaveDialog
    private static readonly Guid CLSID_FileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
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
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
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
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IShellItemArray ppenum);
    }

    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetAttributes(uint AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(int sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    // FOS_* flags
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;
    private const uint FOS_ALLOWMULTISELECT = 0x00000200;

    // SIGDN
    private const int SIGDN_FILESYSPATH = unchecked((int)0x80058000);
    private const int ERROR_CANCELLED_HRESULT = unchecked((int)0x800704C7);
    private const uint FOS_NOCHANGEDIR = 0x00000008;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    // kernel32: 设置 per-drive current directory
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetCurrentDirectory([MarshalAs(UnmanagedType.LPWStr)] string lpPathName);

    // 显示文件夹选择对话框
    public static string? Show(string title, string? initialPath = null)
    {
        try
        {
            // 关键修复：在创建对话框之前，将进程当前目录设为目标盘的根目录。
            // 这是因为 IFileOpenDialog + FOS_PICKFOLDERS 在面包屑导航中点击盘符时，
            // Windows Shell 会读取该驱动器的 per-drive current directory 来决定目标。
            // 如果进程在 D 盘的深层目录运行，per-drive directory 就是那个深层目录，
            // 点击 D: 盘符就会跳到程序目录而非 D:\ 根目录。
            //
            // 策略：
            // 1. 保存当前目录
            // 2. 设为 D:\ 根目录（通过 kernel32 SetCurrentDirectory 改变 per-drive dir）
            // 3. 创建并显示对话框
            // 4. 恢复当前目录

            var savedDir = Environment.CurrentDirectory;
            string? savedRootDir = null;

            // 获取保存目录的盘符根目录
            var savedRoot = Path.GetPathRoot(savedDir);
            if (!string.IsNullOrEmpty(savedRoot) &&
                !string.Equals(savedDir, savedRoot, StringComparison.OrdinalIgnoreCase))
            {
                // 先进入盘符根目录，重置 per-drive directory
                savedRootDir = savedRoot;
                SetCurrentDirectory(savedRoot);
            }

            // 同时，如果 initialPath 在另一个盘，也要处理
            if (!string.IsNullOrEmpty(initialPath) && System.IO.Directory.Exists(initialPath))
            {
                var initRoot = Path.GetPathRoot(initialPath);
                if (!string.IsNullOrEmpty(initRoot) &&
                    !string.Equals(initRoot, savedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    SetCurrentDirectory(initRoot);
                }
            }

            var dialogType = Type.GetTypeFromCLSID(CLSID_FileOpenDialog);
            if (dialogType == null)
            {
                // 恢复当前目录
                if (savedRootDir != null) SetCurrentDirectory(savedDir);
                return Fallback(title, initialPath);
            }

            var dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;

            // 设置为文件夹选择模式
            uint options;
            dialog.GetOptions(out options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

            // 设置标题
            dialog.SetTitle(title);

            // 设置按钮文字
            dialog.SetOkButtonLabel("选择此文件夹");

            // 使用 SetFolder 设置初始导航位置
            if (!string.IsNullOrEmpty(initialPath) && System.IO.Directory.Exists(initialPath))
            {
                try
                {
                    int hr = SHCreateItemFromParsingName(initialPath, IntPtr.Zero, typeof(IShellItem).GUID, out var folderItem);
                    if (hr == 0 && folderItem != null)
                    {
                        dialog.SetFolder(folderItem);
                        Marshal.ReleaseComObject(folderItem);
                    }
                }
                catch { }
            }

            int result = dialog.Show(IntPtr.Zero);

            // 恢复当前目录
            if (savedRootDir != null) SetCurrentDirectory(savedDir);

            if (result != 0)
            {
                Marshal.ReleaseComObject(dialog);
                return null;
            }

            dialog.GetResult(out var selectedItem);
            if (selectedItem == null)
            {
                Marshal.ReleaseComObject(dialog);
                return null;
            }

            IntPtr pszPath;
            selectedItem.GetDisplayName(SIGDN_FILESYSPATH, out pszPath);
            var path = Marshal.PtrToStringUni(pszPath);
            Marshal.FreeCoTaskMem(pszPath);

            Marshal.ReleaseComObject(selectedItem);
            Marshal.ReleaseComObject(dialog);

            return path;
        }
        catch
        {
            return Fallback(title, initialPath);
        }
    }

    public static List<string>? ShowMultiSelect(string title, string? initialPath = null, IntPtr owner = default)
    {
        var dialogType = Type.GetTypeFromCLSID(CLSID_FileOpenDialog, throwOnError: true)!;
        var dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST
                | FOS_ALLOWMULTISELECT | FOS_NOCHANGEDIR);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("选择文件夹");

            if (Directory.Exists(initialPath) && SHCreateItemFromParsingName(initialPath!, IntPtr.Zero,
                typeof(IShellItem).GUID, out var folderItem) == 0 && folderItem != null)
            {
                try { dialog.SetFolder(folderItem); }
                finally { Marshal.ReleaseComObject(folderItem); }
            }

            var result = dialog.Show(owner);
            if (result == ERROR_CANCELLED_HRESULT) return null;
            Marshal.ThrowExceptionForHR(result);

            dialog.GetResults(out var itemArray);
            try
            {
                itemArray.GetCount(out var count);
                var paths = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    itemArray.GetItemAt(i, out var item);
                    try
                    {
                        item.GetDisplayName(SIGDN_FILESYSPATH, out var name);
                        try
                        {
                            var path = Marshal.PtrToStringUni(name);
                            if (!string.IsNullOrEmpty(path)) paths.Add(path);
                        }
                        finally { Marshal.FreeCoTaskMem(name); }
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return paths;
            }
            finally { Marshal.ReleaseComObject(itemArray); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private static string? Fallback(string title, string? initialPath)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = title,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };
        if (!string.IsNullOrEmpty(initialPath) && System.IO.Directory.Exists(initialPath))
            dialog.SelectedPath = initialPath;
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
