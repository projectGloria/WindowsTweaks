using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsTweaksApp
{
    public class FolderIconResult
    {
        public string RootFolder { get; set; }
        public int ScannedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> UpdatedEntries { get; set; }
        public List<string> Errors { get; set; }

        public FolderIconResult()
        {
            UpdatedEntries = new List<string>();
            Errors = new List<string>();
        }
    }

    internal static class FolderIconApplier
    {
        private const uint FosPickFolders = 0x00000020;
        private const uint FosForceFileSystem = 0x00000040;
        private const uint FosPathMustExist = 0x00000800;
        private const uint SigdnFileSysPath = 0x80058000;
        private const int ErrorCancelled = unchecked((int)0x800704C7);

        private const int ShcneUpdateDir = 0x00001000;
        private const int ShcneUpdateItem = 0x00002000;
        private const int ShcneAssocChanged = 0x08000000;
        private const uint ShcnfIdList = 0x0000;
        private const uint ShcnfPathW = 0x0005;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCom
        {
        }

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig]
            int Show(IntPtr parent);
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
            void AddPlace(IShellItem psi, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        internal static string PickFolder(IntPtr ownerHandle, string title)
        {
            IFileDialog dialog = (IFileDialog)new FileOpenDialogCom();
            IShellItem item = null;
            try
            {
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);

                int hr = dialog.Show(ownerHandle);
                if (hr == ErrorCancelled) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                dialog.GetResult(out item);
                string path;
                item.GetDisplayName(SigdnFileSysPath, out path);
                return path;
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        internal static FolderIconResult ApplySubfolderIcons(string rootFolder, bool includeNested)
        {
            if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder))
                throw new DirectoryNotFoundException("The selected folder could not be found.");

            FolderIconResult result = new FolderIconResult { RootFolder = rootFolder };
            List<string> subfolders = CollectSubfolders(rootFolder, includeNested, result.Errors);

            foreach (string folder in subfolders)
            {
                result.ScannedCount++;
                try
                {
                    string icoPath = FindBestIcon(folder);
                    if (icoPath == null)
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    string icoName = Path.GetFileName(icoPath);
                    ApplyFolderIcon(folder, icoName);
                    result.UpdatedCount++;
                    result.UpdatedEntries.Add(GetRelativeDisplayPath(rootFolder, folder) + " -> " + icoName);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(GetRelativeDisplayPath(rootFolder, folder) + ": " + ex.Message);
                }
            }

            if (result.UpdatedCount > 0)
            {
                try
                {
                    SHChangeNotify(ShcneUpdateDir, ShcnfPathW, rootFolder, IntPtr.Zero);
                    SHChangeNotify(ShcneAssocChanged, ShcnfIdList, null, IntPtr.Zero);
                }
                catch { }
            }

            return result;
        }

        private static List<string> CollectSubfolders(string rootFolder, bool includeNested, List<string> errors)
        {
            List<string> found = new List<string>();
            Queue<string> queue = new Queue<string>();

            try
            {
                string[] immediate = Directory.GetDirectories(rootFolder);
                Array.Sort(immediate, StringComparer.OrdinalIgnoreCase);
                foreach (string dir in immediate)
                {
                    if (IsReparsePoint(dir)) continue;
                    found.Add(dir);
                    if (includeNested) queue.Enqueue(dir);
                }
            }
            catch (Exception ex)
            {
                errors.Add(rootFolder + ": " + ex.Message);
                return found;
            }

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                try
                {
                    string[] children = Directory.GetDirectories(current);
                    Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                    foreach (string child in children)
                    {
                        if (IsReparsePoint(child)) continue;
                        found.Add(child);
                        queue.Enqueue(child);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(GetRelativeDisplayPath(rootFolder, current) + ": " + ex.Message);
                }
            }

            return found;
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static string FindBestIcon(string folderPath)
        {
            string[] icons = Directory.GetFiles(folderPath, "*.ico", SearchOption.TopDirectoryOnly);
            if (icons.Length == 0) return null;

            Array.Sort(icons, StringComparer.OrdinalIgnoreCase);
            string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            foreach (string icon in icons)
            {
                string stem = Path.GetFileNameWithoutExtension(icon);
                if (string.Equals(stem, folderName, StringComparison.OrdinalIgnoreCase))
                    return icon;
            }

            foreach (string icon in icons)
            {
                string fileName = Path.GetFileName(icon);
                if (string.Equals(fileName, "folder.ico", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "icon.ico", StringComparison.OrdinalIgnoreCase))
                    return icon;
            }

            return icons[0];
        }

        private static void ApplyFolderIcon(string folderPath, string icoFileName)
        {
            string iniPath = Path.Combine(folderPath, "desktop.ini");
            string existing = null;

            if (File.Exists(iniPath))
            {
                FileAttributes existingAttrs = File.GetAttributes(iniPath);
                if ((existingAttrs & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    File.SetAttributes(iniPath, FileAttributes.Normal);
                }
                existing = File.ReadAllText(iniPath);
            }

            string updated = BuildDesktopIni(existing, icoFileName);
            File.WriteAllText(iniPath, updated, Encoding.Unicode);
            File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);

            FileAttributes dirAttrs = File.GetAttributes(folderPath);
            if ((dirAttrs & FileAttributes.ReadOnly) == 0)
            {
                File.SetAttributes(folderPath, dirAttrs | FileAttributes.ReadOnly);
            }

            try
            {
                SHChangeNotify(ShcneUpdateItem, ShcnfPathW, folderPath, IntPtr.Zero);
            }
            catch { }
        }

        private static string BuildDesktopIni(string existingContent, string icoFileName)
        {
            string iconLine = "IconResource=" + icoFileName + ",0";
            if (string.IsNullOrEmpty(existingContent))
            {
                return "[.ShellClassInfo]\r\n" + iconLine + "\r\n";
            }

            string[] rawLines = existingContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<string> output = new List<string>();
            bool inShellSection = false;
            bool foundShellSection = false;
            bool wroteIconLine = false;

            for (int i = 0; i < rawLines.Length; i++)
            {
                string line = rawLines[i];
                if (i == rawLines.Length - 1 && line.Length == 0) continue;

                string trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    if (inShellSection && !wroteIconLine)
                    {
                        output.Add(iconLine);
                        wroteIconLine = true;
                    }

                    inShellSection = string.Equals(trimmed, "[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase);
                    if (inShellSection) foundShellSection = true;
                    output.Add(line);
                    continue;
                }

                if (inShellSection)
                {
                    if (trimmed.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!wroteIconLine)
                        {
                            output.Add(iconLine);
                            wroteIconLine = true;
                        }
                        continue;
                    }

                    if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                output.Add(line);
            }

            if (inShellSection && !wroteIconLine)
            {
                output.Add(iconLine);
                wroteIconLine = true;
            }

            if (!foundShellSection)
            {
                if (output.Count > 0 && output[output.Count - 1].Trim().Length > 0)
                    output.Add("");
                output.Add("[.ShellClassInfo]");
                output.Add(iconLine);
            }

            return string.Join("\r\n", output.ToArray()) + "\r\n";
        }

        private static string GetRelativeDisplayPath(string rootFolder, string folderPath)
        {
            string root = rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (folderPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return folderPath.Substring(root.Length + 1);
            return Path.GetFileName(folderPath);
        }
    }
}
