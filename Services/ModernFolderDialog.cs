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
    private const int SIGDN_FILESYSPATH = 4;

    // IFileDialogEvents GUID
    private static readonly Guid IID_IFileDialogEvents = new("973510DB-7D7F-452B-8975-74A85828D354");

    // IFileDialogEvents 实现，拦截盘符导航
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileDialogEvents : IFileDialogEvents
    {
        public int OnFileOk(IFileDialog pfd) => 0;

        public int OnFolderChanging(IFileDialog pfd, IShellItem psiFolder)
        {
            // 获取目标文件夹路径
            IntPtr pszPath;
            psiFolder.GetDisplayName(SIGDN_FILESYSPATH, out pszPath);
            var targetPath = Marshal.PtrToStringUni(pszPath);
            Marshal.FreeCoTaskMem(pszPath);

            if (string.IsNullOrEmpty(targetPath))
                return 0; // S_OK, 允许导航

            // 如果目标是盘符根目录（如 D:\），确保用正确的方式导航
            // 检查是否为纯盘符路径（D:\ 或 D:）
            if (targetPath.Length == 3 && targetPath[1] == ':' && targetPath[2] == '\\')
            {
                // 盘符根目录，允许导航
                return 0;
            }

            // 其他情况，默认允许
            return 0;
        }

        public void OnFolderChange(IFileDialog pfd) { }
        public void OnSelectionChange(IFileDialog pfd) { }
        public void OnShareViolation(IFileDialog pfd, IShellItem psi, out int pResponse) { pResponse = 0; }
        public void OnTypeChange(IFileDialog pfd) { }
        public void OnOverwrite(IFileDialog pfd, IShellItem psi, out int pResponse) { pResponse = 0; }
    }

    [ComImport]
    [Guid("973510DB-7D7F-452B-8975-74A85828D354")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig] int OnFileOk([In] IFileDialog pfd);
        [PreserveSig] int OnFolderChanging([In] IFileDialog pfd, [In] IShellItem psiFolder);
        void OnFolderChange([In] IFileDialog pfd);
        void OnSelectionChange([In] IFileDialog pfd);
        void OnShareViolation([In] IFileDialog pfd, [In] IShellItem psi, out int pResponse);
        void OnTypeChange([In] IFileDialog pfd);
        void OnOverwrite([In] IFileDialog pfd, [In] IShellItem psi, out int pResponse);
    }

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

    // 多选文件夹对话框
    public static List<string>? ShowMultiSelect(string title, string? initialPath = null)
    {
        try
        {
            var dialogType = Type.GetTypeFromCLSID(CLSID_FileOpenDialog);
            if (dialogType == null) return FallbackMultiSelect(title, initialPath);

            var dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;

            uint options;
            dialog.GetOptions(out options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_ALLOWMULTISELECT);

            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("选择文件夹");

            var events = new FileDialogEvents();
            var eventsPtr = Marshal.GetComInterfaceForObject<FileDialogEvents, IFileDialogEvents>(events);
            dialog.Advise(eventsPtr, out uint cookie);

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
            dialog.Unadvise(cookie);
            Marshal.Release(eventsPtr);
            if (result != 0)
            {
                Marshal.ReleaseComObject(dialog);
                return null;
            }

            dialog.GetResults(out var itemArray);
            if (itemArray == null)
            {
                Marshal.ReleaseComObject(dialog);
                return null;
            }

            itemArray.GetCount(out uint count);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                itemArray.GetItemAt(i, out var item);
                IntPtr pszPath;
                item.GetDisplayName(SIGDN_FILESYSPATH, out pszPath);
                var path = Marshal.PtrToStringUni(pszPath);
                Marshal.FreeCoTaskMem(pszPath);
                Marshal.ReleaseComObject(item);
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }

            Marshal.ReleaseComObject(itemArray);
            Marshal.ReleaseComObject(dialog);

            return paths;
        }
        catch
        {
            return FallbackMultiSelect(title, initialPath);
        }
    }

    private static List<string>? FallbackMultiSelect(string title, string? initialPath)
    {
        var single = Show(title, initialPath);
        return single != null ? new List<string> { single } : null;
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
