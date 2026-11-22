using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WindowsTweaksApp
{
    internal static class ShortcutIntegration
    {
        internal const string HelperName = "CustomShortcutCreator.exe";
        internal const string StorageName = @"projectGloria\WindowsTweaks\CustomShortcuts";
        private const string VerbKey = @"Directory\shell\projectGloria.WindowsTweaks.CreateCustomShortcut";
        private const string VerbLabel = "Create custom shortcut";
        private static readonly Guid LocalAppData = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

        internal static bool IsEnabled()
        {
            using (SafeFileHandle token = OpenExplorerToken())
            using (WindowsIdentity user = new WindowsIdentity(token.DangerousGetHandle()))
            using (RegistryKey key = Registry.Users.OpenSubKey(user.User.Value + "_Classes\\" + VerbKey))
            {
                if (key == null) return false;
                string helper = GetHelperPath(token);
                using (RegistryKey command = key.OpenSubKey("command"))
                {
                    return command != null && File.Exists(helper) &&
                        string.Equals(command.GetValue(null) as string, GetFolderCommand(helper),
                            StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            using (SafeFileHandle token = OpenExplorerToken())
            using (WindowsIdentity user = new WindowsIdentity(token.DangerousGetHandle()))
            {
                string helper = enabled ? GetHelperPath(token) : null;
                // Files and registry keys in the Explorer user's profile are changed with that user's token.
                using (user.Impersonate())
                using (RegistryKey classes = Registry.Users.OpenSubKey(user.User.Value + "_Classes", true))
                {
                    if (classes == null) throw new InvalidOperationException("Explorer's user registry is unavailable.");
                    if (enabled)
                    {
                        InstallHelper(helper);
                        using (RegistryKey verb = classes.CreateSubKey(VerbKey))
                        using (RegistryKey command = verb.CreateSubKey("command"))
                        {
                            command.SetValue(null, GetFolderCommand(helper));
                            verb.SetValue("MUIVerb", VerbLabel);
                            verb.SetValue("Icon", QuoteArgument(helper) + ",0");
                            verb.SetValue("MultiSelectModel", "Single");
                        }
                    }
                    else
                    {
                        classes.DeleteSubKeyTree(VerbKey, false);
                    }
                }
            }
            NotifyShell();
        }

        internal static void OpenEditor()
        {
            using (SafeFileHandle token = OpenExplorerToken())
            {
                string helper = GetHelperPath(token);
                SafeFileHandle primary;
                // TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY, SecurityImpersonation, TokenPrimary.
                if (!DuplicateTokenEx(token, 0x000B, IntPtr.Zero, 2, 1, out primary))
                    throw new Win32Exception();
                using (primary)
                {
                    IntPtr environment;
                    if (!CreateEnvironmentBlock(out environment, primary, false)) throw new Win32Exception();
                    try
                    {
                        StartupInfo startup = new StartupInfo();
                        startup.Size = Marshal.SizeOf(typeof(StartupInfo));
                        ProcessInformation process;
                        StringBuilder arguments = new StringBuilder(QuoteArgument(helper) + " --edit");
                        const uint CreateUnicodeEnvironment = 0x00000400;
                        if (!CreateProcessWithTokenW(primary, 0, helper, arguments, CreateUnicodeEnvironment,
                            environment, Path.GetDirectoryName(helper), ref startup, out process))
                            throw new Win32Exception();
                        CloseHandle(process.Thread);
                        CloseHandle(process.Process);
                    }
                    finally
                    {
                        DestroyEnvironmentBlock(environment);
                    }
                }
            }
        }

        private static string GetFolderCommand(string helper)
        {
            return QuoteArgument(helper) + " --edit \"%1\"";
        }

        private static void InstallHelper(string helper)
        {
            byte[] payload;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(HelperName))
            using (MemoryStream bytes = new MemoryStream())
            {
                if (resource == null) throw new InvalidOperationException("The shortcut creator is missing from this build.");
                resource.CopyTo(bytes);
                payload = bytes.ToArray();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(helper));
            if (File.Exists(helper))
            {
                if (EqualBytes(File.ReadAllBytes(helper), payload)) return;
                EnsureHelperIsStopped();
            }
            string staged = helper + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
                    file.Write(payload, 0, payload.Length);
                if (File.Exists(helper)) File.Replace(staged, helper, null);
                else File.Move(staged, helper);
            }
            finally
            {
                File.Delete(staged);
            }
        }

        private static void EnsureHelperIsStopped()
        {
            int sessionId;
            using (Process current = Process.GetCurrentProcess()) sessionId = current.SessionId;
            Process[] helpers = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HelperName));
            try
            {
                foreach (Process helper in helpers)
                {
                    if (!helper.HasExited && helper.SessionId == sessionId)
                        throw new IOException("Close Custom Shortcut Creator or wait for the shortcut to finish opening, then try again.");
                }
            }
            finally
            {
                foreach (Process helper in helpers) helper.Dispose();
            }
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        private static string GetHelperPath(SafeFileHandle token)
        {
            Guid folder = LocalAppData;
            IntPtr path;
            int result = SHGetKnownFolderPath(ref folder, 0, token, out path);
            try
            {
                Marshal.ThrowExceptionForHR(result);
                return Path.Combine(Marshal.PtrToStringUni(path), StorageName, HelperName);
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }

        private static SafeFileHandle OpenExplorerToken()
        {
            IntPtr shell = GetShellWindow();
            uint processId;
            if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out processId) == 0)
                throw new InvalidOperationException("Open File Explorer and try again.");
            const uint ProcessQueryLimitedInformation = 0x1000;
            using (SafeFileHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId))
            {
                if (process.IsInvalid) throw new Win32Exception();
                const uint TokenQueryDuplicateImpersonate = 0x000E;
                SafeFileHandle token;
                if (!OpenProcessToken(process, TokenQueryDuplicateImpersonate, out token))
                    throw new Win32Exception();
                return token;
            }
        }

        internal static string QuoteArgument(string value)
        {
            StringBuilder escaped = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }
                escaped.Append('\\', c == '"' ? 2 * slashes + 1 : slashes);
                escaped.Append(c);
                slashes = 0;
            }
            return escaped.Append('\\', 2 * slashes).Append('"').ToString();
        }

        internal static void NotifyShell()
        {
            const uint AssociationsChanged = 0x08000000;
            SHChangeNotify(AssociationsChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, Width, Height, Columns, Rows, Fill, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedBuffer, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process, Thread;
            public uint ProcessId, ThreadId;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(SafeFileHandle process, uint access, out SafeFileHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(SafeFileHandle token, uint access, IntPtr attributes,
            int impersonationLevel, int tokenType, out SafeFileHandle duplicate);
        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeFileHandle token, bool inherit);
        [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessWithTokenW(SafeFileHandle token, uint logonFlags, string application,
            StringBuilder command, uint creationFlags, IntPtr environment, string currentDirectory,
            ref StartupInfo startup, out ProcessInformation process);
        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, SafeFileHandle token, out IntPtr path);
        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint evt, uint flags, IntPtr item1, IntPtr item2);
    }
}
