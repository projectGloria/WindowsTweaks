using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WindowsTweaksApp
{
    public class WatermarkTargetInfo
    {
        public uint TargetRva { get; set; }
        public byte[] OriginalBytes { get; set; }
        public string TargetHex { get; set; }
    }

    public static class WatermarkPatcher
    {
        public const string TaskName = "WindowsTweaks-HideWatermark";
        public static readonly byte[] PatchBytes = new byte[] { 0x33, 0xC0, 0xC3 }; // xor eax, eax; ret

        private static readonly object _resolveLock = new object();
        private static WatermarkTargetInfo _cachedTarget;

        public static string HelperPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"WindowsTweaks\WatermarkPatcher.exe");
            }
        }

        private const uint TH32CS_SNAPMODULE = 0x00000008;
        private const uint TH32CS_SNAPMODULE32 = 0x00000010;
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const uint SPI_GETDESKWALLPAPER = 0x0073;
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_SENDCHANGE = 0x0002;
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MODULEENTRY32W
        {
            public uint dwSize;
            public uint th32ModuleID;
            public uint th32ProcessID;
            public uint GlblcntUsage;
            public uint ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        private struct PeSection
        {
            public uint VirtualAddress;
            public uint VirtualSize;
            public uint RawPointer;
            public uint RawSize;
        }

        public static WatermarkTargetInfo ResolveTarget()
        {
            lock (_resolveLock)
            {
                if (_cachedTarget != null) return _cachedTarget;

                string shell32Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
                if (!File.Exists(shell32Path))
                {
                    throw new FileNotFoundException("shell32.dll not found.", shell32Path);
                }

                byte[] b = File.ReadAllBytes(shell32Path);
                if (b.Length < 0x100 || b[0] != 'M' || b[1] != 'Z')
                {
                    throw new InvalidOperationException("Invalid PE header in shell32.dll.");
                }

                int pe = BitConverter.ToInt32(b, 0x3C);
                ushort magic = BitConverter.ToUInt16(b, pe + 24);
                if (magic != 0x020B)
                {
                    throw new InvalidOperationException("shell32.dll is not PE32+ (64-bit).");
                }

                int numSec = BitConverter.ToUInt16(b, pe + 6);
                int optSize = BitConverter.ToUInt16(b, pe + 20);
                int secOff = pe + 24 + optSize;

                // IMAGE_DIRECTORY_ENTRY_EXCEPTION (index 3) in PE32+ optional header starts at +112
                uint excRva = BitConverter.ToUInt32(b, pe + 24 + 112 + 3 * 8);
                uint excSize = BitConverter.ToUInt32(b, pe + 24 + 112 + 3 * 8 + 4);
                if (excRva == 0 || excSize < 12)
                {
                    throw new InvalidOperationException("Exception directory (.pdata) not found in shell32.dll.");
                }

                PeSection[] secs = new PeSection[numSec];
                for (int i = 0; i < numSec; i++)
                {
                    int o = secOff + i * 40;
                    secs[i] = new PeSection
                    {
                        VirtualSize = BitConverter.ToUInt32(b, o + 8),
                        VirtualAddress = BitConverter.ToUInt32(b, o + 12),
                        RawSize = BitConverter.ToUInt32(b, o + 16),
                        RawPointer = BitConverter.ToUInt32(b, o + 20)
                    };
                }

                int excOff = RvaToFileOffset(secs, excRva);
                if (excOff < 0)
                {
                    throw new InvalidOperationException("Unable to map .pdata RVA to file offset.");
                }

                int pdataCount = (int)(excSize / 12);
                PeSection text = secs[0];
                int tStart = (int)text.RawPointer;
                int tEnd = (int)(text.RawPointer + text.RawSize);

                HashSet<uint> f33108 = FindFunctionsReferencingImm32(b, excOff, pdataCount, text, tStart, tEnd, 33108);
                HashSet<uint> f33109 = FindFunctionsReferencingImm32(b, excOff, pdataCount, text, tStart, tEnd, 33109);
                uint buildFuncRva = IntersectSingleOrFirst(f33108, f33109);
                if (buildFuncRva == 0)
                {
                    throw new InvalidOperationException("Could not locate watermark build-string helper in shell32.dll.");
                }

                HashSet<uint> f33115 = FindFunctionsReferencingImm32(b, excOff, pdataCount, text, tStart, tEnd, 33115);
                HashSet<uint> f33112 = FindFunctionsReferencingImm32(b, excOff, pdataCount, text, tStart, tEnd, 33112);
                uint licFuncRva = IntersectSingleOrFirst(f33115, f33112);

                uint targetRva = 0;
                if (licFuncRva != 0 && licFuncRva == buildFuncRva)
                {
                    targetRva = buildFuncRva;
                }
                else
                {
                    HashSet<uint> buildCallers = FindCallers(b, excOff, pdataCount, text, tStart, tEnd, buildFuncRva);
                    if (licFuncRva != 0)
                    {
                        HashSet<uint> licCallers = FindCallers(b, excOff, pdataCount, text, tStart, tEnd, licFuncRva);
                        targetRva = IntersectSingleOrFirst(buildCallers, licCallers);
                    }
                    if (targetRva == 0 && buildCallers.Count == 1)
                    {
                        foreach (uint c in buildCallers) { targetRva = c; break; }
                    }
                }

                if (targetRva == 0)
                {
                    throw new InvalidOperationException("Could not resolve CDesktopWatermark::s_DesktopBuildPaint in shell32.dll.");
                }

                int targetOff = RvaToFileOffset(secs, targetRva);
                if (targetOff < 0 || targetOff + 3 > b.Length)
                {
                    throw new InvalidOperationException("Resolved watermark target RVA is outside shell32.dll.");
                }

                byte[] orig = new byte[] { b[targetOff], b[targetOff + 1], b[targetOff + 2] };
                _cachedTarget = new WatermarkTargetInfo
                {
                    TargetRva = targetRva,
                    OriginalBytes = orig,
                    TargetHex = "0x" + targetRva.ToString("X")
                };
                return _cachedTarget;
            }
        }

        private static int RvaToFileOffset(PeSection[] secs, uint rva)
        {
            for (int i = 0; i < secs.Length; i++)
            {
                PeSection s = secs[i];
                uint span = Math.Max(s.VirtualSize, s.RawSize);
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + span)
                {
                    return (int)(s.RawPointer + (rva - s.VirtualAddress));
                }
            }
            return -1;
        }

        private static uint FindEnclosingFunction(byte[] b, int excOff, int pdataCount, uint rva)
        {
            int lo = 0;
            int hi = pdataCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int entryOff = excOff + mid * 12;
                uint beg = BitConverter.ToUInt32(b, entryOff);
                uint end = BitConverter.ToUInt32(b, entryOff + 4);
                if (rva < beg) hi = mid - 1;
                else if (rva >= end) lo = mid + 1;
                else return beg;
            }
            return 0;
        }

        private static HashSet<uint> FindFunctionsReferencingImm32(byte[] b, int excOff, int pdataCount, PeSection text, int tStart, int tEnd, uint imm)
        {
            HashSet<uint> funcs = new HashSet<uint>();
            byte b0 = (byte)(imm & 0xFF);
            byte b1 = (byte)((imm >> 8) & 0xFF);
            byte b2 = (byte)((imm >> 16) & 0xFF);
            byte b3 = (byte)((imm >> 24) & 0xFF);

            for (int i = tStart; i <= tEnd - 4; i++)
            {
                if (b[i] == b0 && b[i + 1] == b1 && b[i + 2] == b2 && b[i + 3] == b3)
                {
                    uint rva = text.VirtualAddress + (uint)(i - tStart);
                    uint fn = FindEnclosingFunction(b, excOff, pdataCount, rva);
                    if (fn != 0) funcs.Add(fn);
                }
            }
            return funcs;
        }

        private static HashSet<uint> FindCallers(byte[] b, int excOff, int pdataCount, PeSection text, int tStart, int tEnd, uint calleeRva)
        {
            HashSet<uint> callers = new HashSet<uint>();
            for (int i = tStart; i <= tEnd - 5; i++)
            {
                if (b[i] == 0xE8)
                {
                    uint nextRva = text.VirtualAddress + (uint)(i - tStart + 5);
                    int rel = BitConverter.ToInt32(b, i + 1);
                    if ((uint)(nextRva + rel) == calleeRva)
                    {
                        uint callRva = text.VirtualAddress + (uint)(i - tStart);
                        uint fn = FindEnclosingFunction(b, excOff, pdataCount, callRva);
                        if (fn != 0) callers.Add(fn);
                    }
                }
            }
            return callers;
        }

        private static uint IntersectSingleOrFirst(HashSet<uint> a, HashSet<uint> bSet)
        {
            foreach (uint x in a)
            {
                if (bSet.Contains(x)) return x;
            }
            return 0;
        }

        private static void EnableDebugPrivilege()
        {
            IntPtr hToken = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken)) return;
                LUID luid;
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid)) return;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;
                AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            finally
            {
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }
        }

        private static IntPtr FindModuleBase(int pid, string moduleName)
        {
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return IntPtr.Zero;

            try
            {
                MODULEENTRY32W me = new MODULEENTRY32W();
                me.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));
                if (Module32FirstW(snap, ref me))
                {
                    do
                    {
                        if (string.Equals(me.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            return me.modBaseAddr;
                        }
                    }
                    while (Module32NextW(snap, ref me));
                }
            }
            finally
            {
                CloseHandle(snap);
            }
            return IntPtr.Zero;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        public static void GetMemoryStatus(out int explorerCount, out int patchedCount, out string resolvedHex)
        {
            WatermarkTargetInfo info = ResolveTarget();
            resolvedHex = info.TargetHex;
            explorerCount = 0;
            patchedCount = 0;

            EnableDebugPrivilege();
            Process[] procs = Process.GetProcessesByName("explorer");
            foreach (Process p in procs)
            {
                try
                {
                    IntPtr modBase = FindModuleBase(p.Id, "shell32.dll");
                    if (modBase == IntPtr.Zero) continue;
                    explorerCount++;

                    IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (hProc == IntPtr.Zero) continue;
                    try
                    {
                        IntPtr addr = new IntPtr(modBase.ToInt64() + info.TargetRva);
                        byte[] buf = new byte[3];
                        IntPtr bytesRead;
                        if (ReadProcessMemory(hProc, addr, buf, buf.Length, out bytesRead) && bytesRead.ToInt32() == 3)
                        {
                            if (BytesEqual(buf, PatchBytes)) patchedCount++;
                        }
                    }
                    finally
                    {
                        CloseHandle(hProc);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }

        public static int PatchRunningExplorer(bool apply)
        {
            WatermarkTargetInfo info = ResolveTarget();
            byte[] desired = apply ? PatchBytes : info.OriginalBytes;
            int updated = 0;

            EnableDebugPrivilege();
            Process[] procs = Process.GetProcessesByName("explorer");
            foreach (Process p in procs)
            {
                try
                {
                    IntPtr modBase = FindModuleBase(p.Id, "shell32.dll");
                    if (modBase == IntPtr.Zero) continue;

                    IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (hProc == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Failed to open explorer.exe (PID " + p.Id + "), Win32 error " + Marshal.GetLastWin32Error());
                    }

                    try
                    {
                        IntPtr addr = new IntPtr(modBase.ToInt64() + info.TargetRva);
                        byte[] cur = new byte[3];
                        IntPtr bytesRead;
                        if (!ReadProcessMemory(hProc, addr, cur, cur.Length, out bytesRead) || bytesRead.ToInt32() != 3)
                        {
                            throw new InvalidOperationException("ReadProcessMemory failed in explorer.exe (PID " + p.Id + ").");
                        }

                        if (BytesEqual(cur, desired))
                        {
                            updated++;
                            continue;
                        }

                        if (!BytesEqual(cur, info.OriginalBytes) && !BytesEqual(cur, PatchBytes))
                        {
                            throw new InvalidOperationException(string.Format(
                                "Unexpected bytes at shell32+{0}: {1:X2} {2:X2} {3:X2}",
                                info.TargetHex, cur[0], cur[1], cur[2]));
                        }

                        uint oldProt;
                        if (!VirtualProtectEx(hProc, addr, new UIntPtr(3), PAGE_EXECUTE_READWRITE, out oldProt))
                        {
                            throw new InvalidOperationException("VirtualProtectEx failed, Win32 error " + Marshal.GetLastWin32Error());
                        }

                        try
                        {
                            IntPtr written;
                            if (!WriteProcessMemory(hProc, addr, desired, desired.Length, out written) || written.ToInt32() != 3)
                            {
                                throw new InvalidOperationException("WriteProcessMemory failed, Win32 error " + Marshal.GetLastWin32Error());
                            }
                            FlushInstructionCache(hProc, addr, new UIntPtr(3));
                            updated++;
                        }
                        finally
                        {
                            uint dummy;
                            VirtualProtectEx(hProc, addr, new UIntPtr(3), oldProt, out dummy);
                        }
                    }
                    finally
                    {
                        CloseHandle(hProc);
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }

            return updated;
        }

        public static void RefreshDesktopWallpaper()
        {
            try
            {
                StringBuilder sb = new StringBuilder(260);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
                {
                    string wall = sb.ToString();
                    if (!string.IsNullOrEmpty(wall) && File.Exists(wall))
                    {
                        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, wall, SPIF_SENDCHANGE);
                    }
                }
            }
            catch { }

            try
            {
                IntPtr progman = FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                {
                    RedrawWindow(progman, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
                }

                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    StringBuilder cls = new StringBuilder(64);
                    if (GetClassName(hWnd, cls, cls.Capacity) > 0 && cls.ToString() == "WorkerW")
                    {
                        RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        public static bool IsLogonTaskInstalled()
        {
            if (!File.Exists(HelperPath)) return false;
            string taskFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"Tasks\" + TaskName);
            return File.Exists(taskFile);
        }

        public static void InstallLogonTask()
        {
            string dir = Path.GetDirectoryName(HelperPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("WatermarkPatcher.exe"))
            {
                if (s == null)
                {
                    throw new InvalidOperationException("Embedded resource WatermarkPatcher.exe not found.");
                }
                using (FileStream fs = new FileStream(HelperPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    s.CopyTo(fs);
                }
            }

            string tr = "\\\"" + HelperPath + "\\\" --apply";
            string args = string.Format("/Create /TN \"{0}\" /TR \"{1}\" /SC ONLOGON /RL HIGHEST /F", TaskName, tr);
            RunSchtasks(args, true);
        }

        public static void UninstallLogonTask()
        {
            RunSchtasks(string.Format("/Delete /TN \"{0}\" /F", TaskName), false);
            try
            {
                if (File.Exists(HelperPath)) File.Delete(HelperPath);
            }
            catch { }
        }

        private static void RunSchtasks(string arguments, bool throwOnFailure)
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", arguments);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (throwOnFailure && p.ExitCode != 0)
                {
                    string msg = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                    throw new InvalidOperationException("schtasks failed: " + msg);
                }
            }
        }

#if WATERMARK_CLI
        [STAThread]
        public static int Main(string[] args)
        {
            bool revert = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--revert", StringComparison.OrdinalIgnoreCase))
                    {
                        revert = true;
                        break;
                    }
                }
            }

            try
            {
                if (revert)
                {
                    PatchRunningExplorer(false);
                    RefreshDesktopWallpaper();
                    return 0;
                }

                for (int attempt = 0; attempt < 60; attempt++)
                {
                    try
                    {
                        if (PatchRunningExplorer(true) > 0)
                        {
                            Thread.Sleep(800);
                            RefreshDesktopWallpaper();
                            return 0;
                        }
                    }
                    catch { }
                    Thread.Sleep(500);
                }
            }
            catch { }
            return 1;
        }
#endif
    }
}
