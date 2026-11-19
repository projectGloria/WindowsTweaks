using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using Microsoft.Win32;

namespace WindowsTweaksApp
{
    public enum TweakState
    {
        Default,
        Partial,
        Applied
    }

    public class TweakStatus
    {
        public TweakState State { get; set; }
        public string Detail { get; set; }
        public bool ExtraFlag { get; set; }
    }

    public class TweakProfile
    {
        public int Version { get; set; }
        public string ExportedAt { get; set; }
        public bool CleanStartMenu { get; set; }
        public bool DisableWebSearch { get; set; }
        public bool TaskbarCleanup { get; set; }
        public bool AlignTaskbarLeft { get; set; }
        public bool TaskbarEndTask { get; set; }
        public bool ExplorerThisPc { get; set; }
        public bool ShowHiddenFiles { get; set; }
        public bool ShowSuperHidden { get; set; }
        public bool ClassicContextMenu { get; set; }
        public bool DisableSounds { get; set; }
        public bool DisableMouseAccel { get; set; }
        public bool DisableStickyKeys { get; set; }
        public bool DisableTipsAndAds { get; set; }
        public bool PowerShellExecPolicy { get; set; }
        public bool UltimatePowerPlan { get; set; }
    }

    public class CardMeta
    {
        public Border Card { get; set; }
        public string Category { get; set; }
        public string SearchText { get; set; }
    }

    public static class NativeMethods
    {
        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

        public static void NotifyShell()
        {
            try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        public static void SetMouseAccel(bool disable)
        {
            // SPI_SETMOUSE = 0x0004, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE = 0x03
            int[] mouseParams = disable ? new int[] { 0, 0, 0 } : new int[] { 6, 10, 1 };
            try { SystemParametersInfo(0x0004, 0, mouseParams, 0x03); }
            catch { }
        }
    }

    public static class TweakEngine
    {
        public static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal p = new WindowsPrincipal(id);
                    return p.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static string GetOsSummary()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        string ver = Convert.ToString(key.GetValue("DisplayVersion", ""));
                        string build = Convert.ToString(key.GetValue("CurrentBuild", ""));
                        string ubr = Convert.ToString(key.GetValue("UBR", ""));
                        string ed = Convert.ToString(key.GetValue("EditionID", ""));
                        return string.Format("Windows 11 {0}  •  Build {1}.{2}  •  {3}", ver, build, ubr, ed);
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }

        private static object GetReg(RegistryHive hive, RegistryView view, string subKey, string name)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = baseKey.OpenSubKey(subKey, false))
                {
                    if (key == null) return null;
                    return key.GetValue(name, null);
                }
            }
            catch { return null; }
        }

        private static void SetReg(RegistryHive hive, RegistryView view, string subKey, string name, object value, RegistryValueKind kind)
        {
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
            using (RegistryKey key = baseKey.CreateSubKey(subKey))
            {
                if (key == null) throw new InvalidOperationException("Unable to create or open key: " + subKey);
                key.SetValue(name, value, kind);
            }
        }

        private static void DeleteRegValue(RegistryHive hive, RegistryView view, string subKey, string name)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = baseKey.OpenSubKey(subKey, true))
                {
                    if (key != null && key.GetValue(name, null) != null)
                    {
                        key.DeleteValue(name, false);
                    }
                }
            }
            catch { }
        }

        private static int? GetDword(RegistryHive hive, string subKey, string name)
        {
            object v = GetReg(hive, RegistryView.Registry64, subKey, name);
            if (v == null) return null;
            try { return Convert.ToInt32(v); }
            catch { return null; }
        }

        private static string GetStr(RegistryHive hive, string subKey, string name)
        {
            object v = GetReg(hive, RegistryView.Registry64, subKey, name);
            return v == null ? "" : Convert.ToString(v);
        }

        private static string RunCmdOutput(string fileName, string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return output + (string.IsNullOrEmpty(err) ? "" : "\n" + err);
            }
        }

        // 1. Clean Start Menu (Pinned Only)
        public static TweakStatus GetStartMenuStatus()
        {
            bool c1 = GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoStartMenuMorePrograms") == 1;
            bool c2 = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoStartMenuMorePrograms") == 1;
            bool c3 = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideAppList") == 1;
            bool c4 = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideRecommendedSection") == 1;
            bool c5 = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\PolicyManager\current\device\Education", "IsEducationEnvironment") == 1;
            bool c6 = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection") == 1;

            int count = (c1 ? 1 : 0) + (c2 ? 1 : 0) + (c3 ? 1 : 0) + (c4 ? 1 : 0) + (c5 ? 1 : 0) + (c6 ? 1 : 0);
            if (count == 6)
                return new TweakStatus { State = TweakState.Applied, Detail = "All Apps, Categories & Recommended hidden (Pinned only)" };
            if (count > 0)
                return new TweakStatus { State = TweakState.Partial, Detail = string.Format("{0}/6 Start Menu policy keys active", count) };
            return new TweakStatus { State = TweakState.Default, Detail = "Default Windows 11 Start Menu" };
        }

        public static void ApplyStartMenu()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoStartMenuMorePrograms", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoStartMenuMorePrograms", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideAppList", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideRecommendedSection", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideRecentlyAddedApps", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PolicyManager\current\device\Education", "IsEducationEnvironment", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecentlyAddedApps", 1, RegistryValueKind.DWord);

            string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_TrackDocs", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_TrackProgs", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_IrisRecommendations", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_AccountNotifications", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_Layout", 1, RegistryValueKind.DWord);
        }

        public static void RevertStartMenu()
        {
            DeleteRegValue(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoStartMenuMorePrograms");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoStartMenuMorePrograms");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideAppList");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideRecommendedSection");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideRecentlyAddedApps");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PolicyManager\current\device\Education", "IsEducationEnvironment");
            DeleteRegValue(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecentlyAddedApps");

            string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_TrackDocs", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_TrackProgs", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_IrisRecommendations", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_AccountNotifications", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Start_Layout", 0, RegistryValueKind.DWord);
        }

        // 2. Disable Bing / Web Search in Start Menu
        public static TweakStatus GetWebSearchStatus()
        {
            bool cu = GetDword(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") == 1;
            bool lm = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") == 1;
            bool bing = GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled") == 0;
            if (cu && lm && bing)
                return new TweakStatus { State = TweakState.Applied, Detail = "Start Menu search is local-only (Bing disabled)" };
            if (cu || lm || bing)
                return new TweakStatus { State = TweakState.Partial, Detail = "Some web search policy keys active" };
            return new TweakStatus { State = TweakState.Default, Detail = "Bing web suggestions enabled in Start search" };
        }

        public static void ApplyWebSearch()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, RegistryValueKind.DWord);
        }

        public static void RevertWebSearch()
        {
            DeleteRegValue(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions");
            DeleteRegValue(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled");
        }

        // 3. Taskbar Cleanup
        public static TweakStatus GetTaskbarCleanupStatus()
        {
            string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            bool noSearch = GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode") == 0;
            bool noTaskView = GetDword(RegistryHive.CurrentUser, adv, "ShowTaskViewButton") == 0;
            bool noWidgets = GetDword(RegistryHive.CurrentUser, adv, "TaskbarDa") == 0;
            bool alignLeft = GetDword(RegistryHive.CurrentUser, adv, "TaskbarAl") == 0;

            if (noSearch && noTaskView && noWidgets)
                return new TweakStatus { State = TweakState.Applied, Detail = "Search, Task View & Widgets hidden" + (alignLeft ? " • Left-aligned" : ""), ExtraFlag = alignLeft };
            if (noSearch || noTaskView || noWidgets)
                return new TweakStatus { State = TweakState.Partial, Detail = "Some taskbar items hidden", ExtraFlag = alignLeft };
            return new TweakStatus { State = TweakState.Default, Detail = "Default taskbar buttons visible", ExtraFlag = alignLeft };
        }

        public static void ApplyTaskbarCleanup(bool alignLeft)
        {
            string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "ShowTaskViewButton", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "TaskbarDa", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "TaskbarMn", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "TaskbarAl", alignLeft ? 0 : 1, RegistryValueKind.DWord);
            NativeMethods.NotifyShell();
        }

        public static void RevertTaskbarCleanup()
        {
            string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 2, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "ShowTaskViewButton", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "TaskbarDa", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "TaskbarAl", 1, RegistryValueKind.DWord);
            NativeMethods.NotifyShell();
        }

        // 4. Enable "End Task" in Taskbar Right-Click
        public static TweakStatus GetEndTaskStatus()
        {
            bool enabled = GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask") == 1;
            if (enabled)
                return new TweakStatus { State = TweakState.Applied, Detail = "\"End task\" option enabled in taskbar right-click menu" };
            return new TweakStatus { State = TweakState.Default, Detail = "Default taskbar context menu (no End task)" };
        }

        public static void ApplyEndTask()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1, RegistryValueKind.DWord);
        }

        public static void RevertEndTask()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 0, RegistryValueKind.DWord);
        }

        // 5. File Explorer Open to "This PC"
        public static TweakStatus GetExplorerThisPcStatus()
        {
            int? launchTo = GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo");
            if (launchTo == 1)
                return new TweakStatus { State = TweakState.Applied, Detail = "File Explorer opens directly to This PC" };
            return new TweakStatus { State = TweakState.Default, Detail = "File Explorer opens to Home / Quick Access" };
        }

        public static void ApplyExplorerThisPc()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1, RegistryValueKind.DWord);
            NativeMethods.NotifyShell();
        }

        public static void RevertExplorerThisPc()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 2, RegistryValueKind.DWord);
            NativeMethods.NotifyShell();
        }

        // 6. Show Hidden Files & Extensions
        public static TweakStatus GetHiddenFilesStatus()
        {
            string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            int? hidden = GetDword(RegistryHive.CurrentUser, adv, "Hidden");
            int? hideExt = GetDword(RegistryHive.CurrentUser, adv, "HideFileExt");
            int? superHidden = GetDword(RegistryHive.CurrentUser, adv, "ShowSuperHidden");
            bool isSuper = (superHidden == 1);

            if (hidden == 1 && hideExt == 0)
                return new TweakStatus { State = TweakState.Applied, Detail = "Hidden files & extensions visible" + (isSuper ? " + Protected OS files" : ""), ExtraFlag = isSuper };
            if (hidden == 1 || hideExt == 0)
                return new TweakStatus { State = TweakState.Partial, Detail = string.Format("Hidden={0}, Extensions={1}", hidden == 1 ? "Shown" : "Hidden", hideExt == 0 ? "Shown" : "Hidden"), ExtraFlag = isSuper };
            return new TweakStatus { State = TweakState.Default, Detail = "Hidden files & file extensions are hidden", ExtraFlag = isSuper };
        }

        public static void ApplyHiddenFiles(bool includeSuperHidden)
        {
            string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Hidden", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "HideFileExt", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "ShowSuperHidden", includeSuperHidden ? 1 : 0, RegistryValueKind.DWord);
            NativeMethods.NotifyShell();
        }

        public static void RevertHiddenFiles()
        {
            string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "Hidden", 2, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "HideFileExt", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, adv, "ShowSuperHidden", 0, RegistryValueKind.DWord);
            NativeMethods.NotifyShell();
        }

        // 7. Classic Right-Click Context Menu
        public static TweakStatus GetContextMenuStatus()
        {
            string subKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey, false))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("", null);
                        if (v != null && Convert.ToString(v) == "")
                            return new TweakStatus { State = TweakState.Applied, Detail = "Full classic Windows 10 right-click menu enabled" };
                        return new TweakStatus { State = TweakState.Partial, Detail = "CLSID exists with non-empty default value" };
                    }
                }
            }
            catch { }
            return new TweakStatus { State = TweakState.Default, Detail = "Windows 11 compact context menu" };
        }

        public static void ApplyContextMenu()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", RegistryValueKind.String);
        }

        public static void RevertContextMenu()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", false); }
            catch { }
        }

        // 8. Disable Windows & Startup Sounds
        public static TweakStatus GetSoundsStatus()
        {
            string scheme = GetStr(RegistryHive.CurrentUser, @"AppEvents\Schemes", "");
            int? boot = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation", "DisableStartupSound");
            int activeSounds = 0;

            try
            {
                using (RegistryKey apps = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps", false))
                {
                    if (apps != null)
                    {
                        foreach (string appName in apps.GetSubKeyNames())
                        {
                            using (RegistryKey appKey = apps.OpenSubKey(appName, false))
                            {
                                if (appKey == null) continue;
                                foreach (string evName in appKey.GetSubKeyNames())
                                {
                                    using (RegistryKey curKey = appKey.OpenSubKey(evName + @"\.Current", false))
                                    {
                                        if (curKey == null) continue;
                                        string val = Convert.ToString(curKey.GetValue("", "") ?? "");
                                        if (!string.IsNullOrEmpty(val)) activeSounds++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (scheme == ".None" && activeSounds == 0 && boot == 1)
                return new TweakStatus { State = TweakState.Applied, Detail = "All system & startup sounds muted" };
            if (scheme == ".None" || activeSounds == 0 || boot == 1)
                return new TweakStatus { State = TweakState.Partial, Detail = string.Format("Scheme={0}, ActiveSounds={1}, StartupMuted={2}", scheme, activeSounds, boot == 1) };
            return new TweakStatus { State = TweakState.Default, Detail = string.Format("{0} active Windows sound event(s)", activeSounds) };
        }

        public static void ApplySounds()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"AppEvents\Schemes", "", ".None", RegistryValueKind.String);
            using (RegistryKey apps = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps", true))
            {
                if (apps != null)
                {
                    foreach (string appName in apps.GetSubKeyNames())
                    {
                        using (RegistryKey appKey = apps.OpenSubKey(appName, true))
                        {
                            if (appKey == null) continue;
                            foreach (string evName in appKey.GetSubKeyNames())
                            {
                                using (RegistryKey curKey = appKey.OpenSubKey(evName + @"\.Current", true))
                                {
                                    if (curKey != null) curKey.SetValue("", "", RegistryValueKind.String);
                                }
                            }
                        }
                    }
                }
            }
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation", "DisableStartupSound", 1, RegistryValueKind.DWord);
        }

        public static void RevertSounds()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"AppEvents\Schemes", "", ".Default", RegistryValueKind.String);
            using (RegistryKey apps = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps", true))
            {
                if (apps != null)
                {
                    foreach (string appName in apps.GetSubKeyNames())
                    {
                        using (RegistryKey appKey = apps.OpenSubKey(appName, true))
                        {
                            if (appKey == null) continue;
                            foreach (string evName in appKey.GetSubKeyNames())
                            {
                                using (RegistryKey defKey = appKey.OpenSubKey(evName + @"\.Default", false))
                                using (RegistryKey curKey = appKey.OpenSubKey(evName + @"\.Current", true))
                                {
                                    if (defKey != null && curKey != null)
                                    {
                                        object defVal = defKey.GetValue("", null);
                                        if (defVal != null) curKey.SetValue("", defVal, RegistryValueKind.String);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation", "DisableStartupSound", 0, RegistryValueKind.DWord);
        }

        // 9. Disable Mouse Acceleration
        public static TweakStatus GetMouseAccelStatus()
        {
            string s = GetStr(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed");
            string t1 = GetStr(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1");
            string t2 = GetStr(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2");
            if (s == "0" && t1 == "0" && t2 == "0")
                return new TweakStatus { State = TweakState.Applied, Detail = "Enhance pointer precision OFF (1:1 raw feel)" };
            return new TweakStatus { State = TweakState.Default, Detail = "Mouse acceleration enabled (Windows default)" };
        }

        public static void ApplyMouseAccel()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String);
            NativeMethods.SetMouseAccel(true);
        }

        public static void RevertMouseAccel()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Mouse", "MouseSpeed", "1", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Mouse", "MouseThreshold1", "6", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Mouse", "MouseThreshold2", "10", RegistryValueKind.String);
            NativeMethods.SetMouseAccel(false);
        }

        // 10. Disable Sticky Keys Popup
        public static TweakStatus GetStickyKeysStatus()
        {
            string sticky = GetStr(RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags");
            int val;
            bool hotkeyOff = int.TryParse(sticky, out val) && ((val & 4) == 0);
            if (hotkeyOff)
                return new TweakStatus { State = TweakState.Applied, Detail = "5x Shift Sticky Keys & Filter Keys popups disabled" };
            return new TweakStatus { State = TweakState.Default, Detail = "Sticky Keys 5x Shift shortcut popup enabled" };
        }

        public static void ApplyStickyKeys()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Accessibility\ToggleKeys", "Flags", "58", RegistryValueKind.String);
        }

        public static void RevertStickyKeys()
        {
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Accessibility\StickyKeys", "Flags", "510", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Accessibility\Keyboard Response", "Flags", "126", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Accessibility\ToggleKeys", "Flags", "62", RegistryValueKind.String);
        }

        // 11. Disable Windows Tips, Ads & "Finish Setting Up" Nags
        private static readonly string[] CdmKeys = new string[]
        {
            "SubscribedContent-338388Enabled",
            "SubscribedContent-338389Enabled",
            "SubscribedContent-338393Enabled",
            "SubscribedContent-353694Enabled",
            "SubscribedContent-353696Enabled",
            "SubscribedContent-310093Enabled",
            "SoftLandingEnabled",
            "RotatingLockScreenOverlayEnabled"
        };

        public static TweakStatus GetTipsAdsStatus()
        {
            string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            int offCount = 0;
            foreach (string k in CdmKeys)
            {
                if (GetDword(RegistryHive.CurrentUser, cdm, k) == 0) offCount++;
            }
            bool scoobeOff = GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled") == 0;
            bool adOff = GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled") == 0;
            if (scoobeOff) offCount++;
            if (adOff) offCount++;

            int total = CdmKeys.Length + 2;
            if (offCount == total)
                return new TweakStatus { State = TweakState.Applied, Detail = "Lock screen tips, Settings suggestions & setup nags disabled" };
            if (offCount > 0)
                return new TweakStatus { State = TweakState.Partial, Detail = string.Format("{0}/{1} ad & suggestion flags disabled", offCount, total) };
            return new TweakStatus { State = TweakState.Default, Detail = "Windows tips, ads & welcome nags enabled" };
        }

        public static void ApplyTipsAds()
        {
            string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            foreach (string k in CdmKeys)
                SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, cdm, k, 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, RegistryValueKind.DWord);
        }

        public static void RevertTipsAds()
        {
            string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            foreach (string k in CdmKeys)
                SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, cdm, k, 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 1, RegistryValueKind.DWord);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1, RegistryValueKind.DWord);
        }

        // 12. PowerShell Execution Policy
        public static TweakStatus GetExecPolicyStatus()
        {
            string p64 = Convert.ToString(GetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy") ?? "");
            string p32 = Convert.ToString(GetReg(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy") ?? "");
            string pcu = Convert.ToString(GetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy") ?? "");

            if (p64 == "RemoteSigned" && p32 == "RemoteSigned" && pcu == "RemoteSigned")
                return new TweakStatus { State = TweakState.Applied, Detail = "RemoteSigned set for x64, x86 & CurrentUser" };
            if (p64 == "RemoteSigned" || p32 == "RemoteSigned" || pcu == "RemoteSigned")
                return new TweakStatus { State = TweakState.Partial, Detail = string.Format("x64={0}, x86={1}, User={2}", p64, p32, pcu) };
            return new TweakStatus { State = TweakState.Default, Detail = "Default (Restricted / Undefined)" };
        }

        public static void ApplyExecPolicy()
        {
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy", "RemoteSigned", RegistryValueKind.String);
            SetReg(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy", "RemoteSigned", RegistryValueKind.String);
            SetReg(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy", "RemoteSigned", RegistryValueKind.String);
        }

        public static void RevertExecPolicy()
        {
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy");
            DeleteRegValue(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy");
            DeleteRegValue(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy");
        }

        // 13. Ultimate Performance Power Plan
        public static TweakStatus GetPowerPlanStatus()
        {
            try
            {
                string active = RunCmdOutput("powercfg.exe", "/getactivescheme");
                if (active.IndexOf("Ultimate Performance", StringComparison.OrdinalIgnoreCase) >= 0)
                    return new TweakStatus { State = TweakState.Applied, Detail = "Active power plan: Ultimate Performance" };
                Match m = Regex.Match(active, @"\(([^)]+)\)");
                string name = m.Success ? m.Groups[1].Value : "Default";
                return new TweakStatus { State = TweakState.Default, Detail = "Active power plan: " + name };
            }
            catch
            {
                return new TweakStatus { State = TweakState.Default, Detail = "Unable to query powercfg" };
            }
        }

        public static void ApplyUltimatePowerPlan()
        {
            string list = RunCmdOutput("powercfg.exe", "/list");
            string guid = null;
            foreach (string line in list.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.IndexOf("Ultimate Performance", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Match m = Regex.Match(line, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                    if (m.Success) { guid = m.Value; break; }
                }
            }
            if (guid == null)
            {
                string dup = RunCmdOutput("powercfg.exe", "-duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61");
                Match m = Regex.Match(dup, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                if (!m.Success) throw new InvalidOperationException("Failed to unlock Ultimate Performance plan: " + dup.Trim());
                guid = m.Value;
            }
            RunCmdOutput("powercfg.exe", "/setactive " + guid);
        }

        public static void RevertUltimatePowerPlan()
        {
            RunCmdOutput("powercfg.exe", "/setactive 381b4222-f694-41f0-9685-ff5bb260df2e");
        }

        public static void CreateRestorePoint()
        {
            string cmd =
                "$ErrorActionPreference = 'Stop'; " +
                "Enable-ComputerRestore -Drive ($env:SystemDrive + '\\') -ErrorAction SilentlyContinue; " +
                "Checkpoint-Computer -Description 'WindowsTweaks Backup' -RestorePointType 'MODIFY_SETTINGS'";
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd + "\"");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "Checkpoint-Computer failed (Windows limits restore points to 1 per 24h by default)." : err.Trim());
                }
            }
        }

        public static void RestartExplorer()
        {
            foreach (string procName in new[] { "StartMenuExperienceHost", "explorer" })
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(procName))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch { }
            }
            Thread.Sleep(600);
            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                Process.Start("explorer.exe");
            }
        }
    }

    public class Program
    {
        private static Window _window;
        private static bool _suppressToggleEvents = false;
        private static string _activeCategory = "All";
        private static readonly BrushConverter _bc = new BrushConverter();

        private static Brush Hex(string h)
        {
            return (Brush)_bc.ConvertFromString(h);
        }

        [STAThread]
        public static void Main()
        {
            Application app = new Application();
            _window = BuildWindow();
            app.Run(_window);
        }

        private static ImageSource LoadEmbeddedIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("AppIcon.ico"))
                {
                    if (s != null)
                    {
                        BitmapDecoder dec = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        if (dec.Frames.Count > 0)
                        {
                            BitmapFrame best = dec.Frames[0];
                            foreach (BitmapFrame f in dec.Frames)
                            {
                                if (f.PixelWidth > best.PixelWidth && f.PixelWidth <= 64) best = f;
                            }
                            return best;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static Window BuildWindow()
        {
            string xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='Windows Tweaks'
        Width='1040' Height='740' MinWidth='900' MinHeight='620'
        WindowStartupLocation='CenterScreen'
        WindowStyle='None' AllowsTransparency='True' Background='Transparent'
        ResizeMode='CanResizeWithGrip'
        FontFamily='Segoe UI' Foreground='#E6EDF7'>

    <Window.Resources>
        <!-- Fluent Pill Toggle Switch -->
        <Style x:Key='ToggleSwitch' TargetType='CheckBox'>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='VerticalAlignment' Value='Center'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='CheckBox'>
                        <Border x:Name='Track' Width='46' Height='24' CornerRadius='12'
                                Background='#2D3748' BorderBrush='#3F4D63' BorderThickness='1'>
                            <Border x:Name='Thumb' Width='18' Height='18' CornerRadius='9'
                                    Background='#CBD5E1' HorizontalAlignment='Left' Margin='2,0,0,0'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsChecked' Value='True'>
                                <Setter TargetName='Track' Property='Background' Value='#2563EB'/>
                                <Setter TargetName='Track' Property='BorderBrush' Value='#3B82F6'/>
                                <Setter TargetName='Thumb' Property='Background' Value='#FFFFFF'/>
                                <Setter TargetName='Thumb' Property='HorizontalAlignment' Value='Right'/>
                                <Setter TargetName='Thumb' Property='Margin' Value='0,0,2,0'/>
                            </Trigger>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='Track' Property='Opacity' Value='0.9'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Dark ComboBox & Items -->
        <Style x:Key='DarkComboBoxItem' TargetType='ComboBoxItem'>
            <Setter Property='Foreground' Value='#E2E8F0'/>
            <Setter Property='Background' Value='#111622'/>
            <Setter Property='Padding' Value='10,7'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='ComboBoxItem'>
                        <Border x:Name='ItemBd' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' CornerRadius='5' Margin='3,1'>
                            <ContentPresenter/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsHighlighted' Value='True'>
                                <Setter TargetName='ItemBd' Property='Background' Value='#1E3A8A'/>
                                <Setter Property='Foreground' Value='#FFFFFF'/>
                            </Trigger>
                            <Trigger Property='IsSelected' Value='True'>
                                <Setter TargetName='ItemBd' Property='Background' Value='#1D4ED8'/>
                                <Setter Property='Foreground' Value='#FFFFFF'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key='DarkComboBox' TargetType='ComboBox'>
            <Setter Property='Foreground' Value='#E2E8F0'/>
            <Setter Property='Background' Value='#111622'/>
            <Setter Property='BorderBrush' Value='#2D3A50'/>
            <Setter Property='BorderThickness' Value='1'/>
            <Setter Property='ItemContainerStyle' Value='{StaticResource DarkComboBoxItem}'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='ComboBox'>
                        <Grid>
                            <ToggleButton x:Name='ToggleButton' Focusable='False'
                                          IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'
                                          ClickMode='Press'>
                                <ToggleButton.Template>
                                    <ControlTemplate TargetType='ToggleButton'>
                                        <Border x:Name='CbBd' Background='#111622' BorderBrush='#2D3A50' BorderThickness='1' CornerRadius='7' Padding='10,6'>
                                            <Grid>
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width='*'/>
                                                    <ColumnDefinition Width='18'/>
                                                </Grid.ColumnDefinitions>
                                                <TextBlock Grid.Column='1' Text='&#xE70D;' FontFamily='Segoe MDL2 Assets' FontSize='10'
                                                           Foreground='#94A3B8' HorizontalAlignment='Right' VerticalAlignment='Center'/>
                                            </Grid>
                                        </Border>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property='IsMouseOver' Value='True'>
                                                <Setter TargetName='CbBd' Property='BorderBrush' Value='#3B82F6'/>
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </ToggleButton.Template>
                            </ToggleButton>
                            <ContentPresenter IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}'
                                              Margin='10,6,28,6' VerticalAlignment='Center' HorizontalAlignment='Left'/>
                            <Popup x:Name='Popup' Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}'
                                   AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
                                <Border MinWidth='{TemplateBinding ActualWidth}' Margin='0,4,0,0'
                                        Background='#111622' BorderBrush='#2D3A50' BorderThickness='1' CornerRadius='8' Padding='2,4'>
                                    <ScrollViewer MaxHeight='220'>
                                        <StackPanel IsItemsHost='True'/>
                                    </ScrollViewer>
                                </Border>
                            </Popup>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Flat Buttons -->
        <Style x:Key='FlatBtn' TargetType='Button'>
            <Setter Property='Background' Value='#2563EB'/>
            <Setter Property='Foreground' Value='#FFFFFF'/>
            <Setter Property='BorderThickness' Value='0'/>
            <Setter Property='Padding' Value='12,7'/>
            <Setter Property='FontSize' Value='12'/>
            <Setter Property='FontWeight' Value='SemiBold'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border x:Name='Bd' Background='{TemplateBinding Background}' CornerRadius='7'
                                BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
                                Padding='{TemplateBinding Padding}'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='Bd' Property='Opacity' Value='0.88'/>
                            </Trigger>
                            <Trigger Property='IsPressed' Value='True'>
                                <Setter TargetName='Bd' Property='Opacity' Value='0.75'/>
                            </Trigger>
                            <Trigger Property='IsEnabled' Value='False'>
                                <Setter TargetName='Bd' Property='Opacity' Value='0.45'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key='SubtleBtn' TargetType='Button' BasedOn='{StaticResource FlatBtn}'>
            <Setter Property='Background' Value='#161E2D'/>
            <Setter Property='Foreground' Value='#D8E1EE'/>
            <Setter Property='BorderBrush' Value='#273247'/>
            <Setter Property='BorderThickness' Value='1'/>
        </Style>

        <Style x:Key='DangerBtn' TargetType='Button' BasedOn='{StaticResource FlatBtn}'>
            <Setter Property='Background' Value='#2A161B'/>
            <Setter Property='Foreground' Value='#FCA5A5'/>
            <Setter Property='BorderBrush' Value='#4C1D24'/>
            <Setter Property='BorderThickness' Value='1'/>
        </Style>

        <Style x:Key='EmeraldBtn' TargetType='Button' BasedOn='{StaticResource FlatBtn}'>
            <Setter Property='Background' Value='#059669'/>
            <Setter Property='Foreground' Value='#FFFFFF'/>
        </Style>

        <Style x:Key='AmberBtn' TargetType='Button' BasedOn='{StaticResource FlatBtn}'>
            <Setter Property='Background' Value='#D97706'/>
            <Setter Property='Foreground' Value='#FFFFFF'/>
            <Setter Property='Padding' Value='10,4'/>
            <Setter Property='FontSize' Value='11.5'/>
        </Style>

        <!-- Sidebar Navigation Button -->
        <Style x:Key='NavBtn' TargetType='Button'>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='Foreground' Value='#94A3B8'/>
            <Setter Property='HorizontalContentAlignment' Value='Left'/>
            <Setter Property='Padding' Value='11,8'/>
            <Setter Property='Margin' Value='0,0,0,3'/>
            <Setter Property='FontSize' Value='12.5'/>
            <Setter Property='FontWeight' Value='Medium'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border x:Name='NavBd' Background='{TemplateBinding Background}' CornerRadius='7' Padding='{TemplateBinding Padding}'>
                            <ContentPresenter HorizontalAlignment='Left' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='NavBd' Property='Background' Value='#161E2E'/>
                                <Setter Property='Foreground' Value='#F1F5F9'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Window Caption Buttons -->
        <Style x:Key='CaptionBtn' TargetType='Button'>
            <Setter Property='Width' Value='38'/>
            <Setter Property='Height' Value='32'/>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='Foreground' Value='#94A3B8'/>
            <Setter Property='FontSize' Value='12'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border x:Name='CapBd' Background='{TemplateBinding Background}' CornerRadius='6'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='CapBd' Property='Background' Value='#1E293B'/>
                                <Setter Property='Foreground' Value='#FFFFFF'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key='CloseCaptionBtn' TargetType='Button' BasedOn='{StaticResource CaptionBtn}'>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border x:Name='CapBd' Background='{TemplateBinding Background}' CornerRadius='6'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='CapBd' Property='Background' Value='#DC2626'/>
                                <Setter Property='Foreground' Value='#FFFFFF'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Tweak Card -->
        <Style x:Key='TweakCard' TargetType='Border'>
            <Setter Property='Background' Value='#151B26'/>
            <Setter Property='BorderBrush' Value='#232D3F'/>
            <Setter Property='BorderThickness' Value='1'/>
            <Setter Property='CornerRadius' Value='10'/>
            <Setter Property='Padding' Value='15,12'/>
            <Setter Property='Margin' Value='0,0,0,9'/>
            <Style.Triggers>
                <Trigger Property='IsMouseOver' Value='True'>
                    <Setter Property='BorderBrush' Value='#334461'/>
                    <Setter Property='Background' Value='#18202D'/>
                </Trigger>
            </Style.Triggers>
        </Style>
    </Window.Resources>

    <!-- Outer Frame -->
    <Border Margin='10' Background='#0D1117' BorderBrush='#253045' BorderThickness='1' CornerRadius='12'>
        <Border.Effect>
            <DropShadowEffect BlurRadius='18' ShadowDepth='2' Opacity='0.55' Color='#000000'/>
        </Border.Effect>

        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height='46'/>
                <RowDefinition Height='*'/>
            </Grid.RowDefinitions>

            <!-- CUSTOM DARK TITLEBAR -->
            <Border x:Name='TitleBar' Grid.Row='0' Background='#0A0D13' BorderBrush='#1C2434' BorderThickness='0,0,0,1' CornerRadius='12,12,0,0'>
                <Grid Margin='14,0,8,0'>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width='*'/>
                        <ColumnDefinition Width='Auto'/>
                    </Grid.ColumnDefinitions>

                    <StackPanel Grid.Column='0' Orientation='Horizontal' VerticalAlignment='Center'>
                        <Image x:Name='ImgTitleIcon' Width='20' Height='20' Margin='0,0,10,0' RenderOptions.BitmapScalingMode='HighQuality'/>
                        <TextBlock Text='Windows Tweaks' FontSize='14' FontWeight='SemiBold' Foreground='#F8FAFC' VerticalAlignment='Center'/>
                        <Border x:Name='BadgeAdmin' CornerRadius='4' Padding='7,2' Margin='12,0,0,0' Background='#064E3B' VerticalAlignment='Center'>
                            <TextBlock x:Name='TxtAdminBadge' Text='ADMINISTRATOR' FontSize='10' FontWeight='Bold' Foreground='#6EE7B7'/>
                        </Border>
                        <TextBlock x:Name='TxtOsInfo' Text='' FontSize='11.5' Foreground='#64748B' Margin='12,0,0,0' VerticalAlignment='Center'/>
                    </StackPanel>

                    <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Center'>
                        <Button x:Name='BtnMin' Content='&#x2014;' Style='{StaticResource CaptionBtn}' Margin='0,0,2,0'/>
                        <Button x:Name='BtnMax' Content='&#x25A1;' Style='{StaticResource CaptionBtn}' Margin='0,0,2,0'/>
                        <Button x:Name='BtnClose' Content='&#x2715;' Style='{StaticResource CloseCaptionBtn}'/>
                    </StackPanel>
                </Grid>
            </Border>

            <!-- BODY -->
            <Grid Grid.Row='1'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='235'/>
                    <ColumnDefinition Width='*'/>
                </Grid.ColumnDefinitions>

                <!-- LEFT SIDEBAR -->
                <Border Grid.Column='0' Background='#0A0D13' BorderBrush='#1C2434' BorderThickness='0,0,1,0' CornerRadius='0,0,0,12' Padding='14'>
                    <Grid>
                        <Grid.RowDefinitions>
                            <RowDefinition Height='Auto'/>
                            <RowDefinition Height='Auto'/>
                            <RowDefinition Height='*'/>
                            <RowDefinition Height='Auto'/>
                        </Grid.RowDefinitions>

                        <!-- Active Summary Card -->
                        <Border Grid.Row='0' Background='#131925' BorderBrush='#222C3E' BorderThickness='1' CornerRadius='9' Padding='12' Margin='0,0,0,12'>
                            <StackPanel>
                                <Grid>
                                    <TextBlock Text='ACTIVE TWEAKS' FontSize='10' FontWeight='Bold' Foreground='#64748B'/>
                                    <TextBlock x:Name='TxtActiveRatio' Text='0 / 13' FontSize='12' FontWeight='Bold' Foreground='#60A5FA' HorizontalAlignment='Right'/>
                                </Grid>
                                <Border Background='#1E293B' Height='6' CornerRadius='3' Margin='0,8,0,0'>
                                    <Border x:Name='BarProgress' Background='#3B82F6' Height='6' CornerRadius='3' HorizontalAlignment='Left' Width='0'/>
                                </Border>
                            </StackPanel>
                        </Border>

                        <!-- Categories -->
                        <StackPanel Grid.Row='1'>
                            <TextBlock Text='CATEGORIES' FontSize='10' FontWeight='Bold' Foreground='#475569' Margin='6,0,0,5'/>
                            <Button x:Name='NavAll' Style='{StaticResource NavBtn}'>
                                <StackPanel Orientation='Horizontal'>
                                    <TextBlock Text='&#xE80F;' FontFamily='Segoe MDL2 Assets' FontSize='14' VerticalAlignment='Center' Margin='0,0,10,0'/>
                                    <TextBlock Text='All Tweaks' FontFamily='Segoe UI' VerticalAlignment='Center'/>
                                </StackPanel>
                            </Button>
                            <Button x:Name='NavShell' Style='{StaticResource NavBtn}'>
                                <StackPanel Orientation='Horizontal'>
                                    <TextBlock Text='&#xE8B7;' FontFamily='Segoe MDL2 Assets' FontSize='14' VerticalAlignment='Center' Margin='0,0,10,0'/>
                                    <TextBlock Text='Shell &amp; Explorer' FontFamily='Segoe UI' VerticalAlignment='Center'/>
                                </StackPanel>
                            </Button>
                            <Button x:Name='NavSystem' Style='{StaticResource NavBtn}'>
                                <StackPanel Orientation='Horizontal'>
                                    <TextBlock Text='&#xE770;' FontFamily='Segoe MDL2 Assets' FontSize='14' VerticalAlignment='Center' Margin='0,0,10,0'/>
                                    <TextBlock Text='System &amp; Performance' FontFamily='Segoe UI' VerticalAlignment='Center'/>
                                </StackPanel>
                            </Button>
                            <Button x:Name='NavTools' Style='{StaticResource NavBtn}'>
                                <StackPanel Orientation='Horizontal'>
                                    <TextBlock Text='&#xE90F;' FontFamily='Segoe MDL2 Assets' FontSize='14' VerticalAlignment='Center' Margin='0,0,10,0'/>
                                    <TextBlock Text='External Tools' FontFamily='Segoe UI' VerticalAlignment='Center'/>
                                </StackPanel>
                            </Button>
                        </StackPanel>

                        <!-- Profiles + Quick Actions -->
                        <StackPanel Grid.Row='3'>
                            <TextBlock Text='SAFETY &amp; PROFILES' FontSize='10' FontWeight='Bold' Foreground='#475569' Margin='6,0,0,6'/>
                            <Button x:Name='BtnRestorePoint' Content='Create Restore Point' Style='{StaticResource SubtleBtn}' Margin='0,0,0,6'/>
                            <Grid Margin='0,0,0,10'>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width='*'/>
                                    <ColumnDefinition Width='6'/>
                                    <ColumnDefinition Width='*'/>
                                </Grid.ColumnDefinitions>
                                <Button x:Name='BtnExport' Grid.Column='0' Content='Export JSON' Style='{StaticResource SubtleBtn}' Padding='6,6' FontSize='11.5'/>
                                <Button x:Name='BtnImport' Grid.Column='2' Content='Import JSON' Style='{StaticResource SubtleBtn}' Padding='6,6' FontSize='11.5'/>
                            </Grid>

                            <TextBlock Text='QUICK ACTIONS' FontSize='10' FontWeight='Bold' Foreground='#475569' Margin='6,0,0,6'/>
                            <Button x:Name='BtnApplyAll' Content='Apply All Tweaks' Style='{StaticResource FlatBtn}' Margin='0,0,0,6'/>
                            <Button x:Name='BtnRestartExplorer' Content='Restart Explorer &amp; Start' Style='{StaticResource SubtleBtn}' Margin='0,0,0,6'/>
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width='*'/>
                                    <ColumnDefinition Width='6'/>
                                    <ColumnDefinition Width='*'/>
                                </Grid.ColumnDefinitions>
                                <Button x:Name='BtnRefresh' Grid.Column='0' Content='Refresh' Style='{StaticResource SubtleBtn}' Padding='6,6' FontSize='11.5'/>
                                <Button x:Name='BtnRevertAll' Grid.Column='2' Content='Revert All' Style='{StaticResource DangerBtn}' Padding='6,6' FontSize='11.5'/>
                            </Grid>
                        </StackPanel>
                    </Grid>
                </Border>

                <!-- RIGHT MAIN AREA -->
                <Grid Grid.Column='1' Margin='20,14,20,14'>
                    <Grid.RowDefinitions>
                        <RowDefinition Height='Auto'/>
                        <RowDefinition Height='Auto'/>
                        <RowDefinition Height='*'/>
                        <RowDefinition Height='110'/>
                    </Grid.RowDefinitions>

                    <!-- Header + Search Bar -->
                    <Grid Grid.Row='0' Margin='0,0,0,10'>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width='*'/>
                            <ColumnDefinition Width='250'/>
                        </Grid.ColumnDefinitions>
                        <StackPanel Grid.Column='0'>
                            <TextBlock x:Name='TxtSectionTitle' Text='All Tweaks' FontSize='20' FontWeight='Bold' Foreground='#F8FAFC'/>
                            <TextBlock Text='Toggle any tweak directly — live registry status updates automatically.' FontSize='11.5' Foreground='#7B8CA5' Margin='0,2,0,0'/>
                        </StackPanel>

                        <Border Grid.Column='1' Background='#131925' BorderBrush='#253045' BorderThickness='1' CornerRadius='8' VerticalAlignment='Center' Padding='10,6'>
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width='Auto'/>
                                    <ColumnDefinition Width='*'/>
                                    <ColumnDefinition Width='Auto'/>
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column='0' Text='&#xE721;' FontFamily='Segoe MDL2 Assets' FontSize='12' Foreground='#64748B' VerticalAlignment='Center' Margin='0,0,8,0'/>
                                <TextBox x:Name='TxtSearch' Grid.Column='1' Background='Transparent' Foreground='#E6EDF7' CaretBrush='#60A5FA'
                                         BorderThickness='0' FontSize='12.5' VerticalAlignment='Center'/>
                                <TextBlock x:Name='TxtSearchPlaceholder' Grid.Column='1' Text='Search tweaks...' Foreground='#475569' FontSize='12.5'
                                           VerticalAlignment='Center' IsHitTestVisible='False'/>
                                <Button x:Name='BtnClearSearch' Grid.Column='2' Content='&#x2715;' Background='Transparent' Foreground='#64748B'
                                        BorderThickness='0' FontSize='10' Cursor='Hand' Visibility='Collapsed' Padding='4,0'/>
                            </Grid>
                        </Border>
                    </Grid>

                    <!-- Restart Needed Banner -->
                    <Border x:Name='BannerRestart' Grid.Row='1' Visibility='Collapsed' Margin='0,0,0,10'
                            Background='#2B1E0B' BorderBrush='#92400E' BorderThickness='1' CornerRadius='8' Padding='12,8'>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width='*'/>
                                <ColumnDefinition Width='Auto'/>
                            </Grid.ColumnDefinitions>
                            <StackPanel Grid.Column='0' Orientation='Horizontal' VerticalAlignment='Center'>
                                <TextBlock Text='&#xE7BA;' FontFamily='Segoe MDL2 Assets' FontSize='14' Foreground='#FBBF24' VerticalAlignment='Center' Margin='0,0,9,0'/>
                                <TextBlock x:Name='TxtRestartMsg' Text='Restart Explorer &amp; Start Menu to see visual changes immediately.'
                                           FontSize='12' Foreground='#FDE68A' VerticalAlignment='Center' TextWrapping='Wrap'/>
                            </StackPanel>
                            <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Center' Margin='10,0,0,0'>
                                <Button x:Name='BtnBannerRestart' Content='Restart Explorer Now' Style='{StaticResource AmberBtn}' Margin='0,0,6,0'/>
                                <Button x:Name='BtnBannerDismiss' Content='&#x2715;' Background='Transparent' Foreground='#FCD34D' BorderThickness='0' Cursor='Hand' Padding='5,2'/>
                            </StackPanel>
                        </Grid>
                    </Border>

                    <!-- TWEAK CARDS -->
                    <ScrollViewer Grid.Row='2' VerticalScrollBarVisibility='Auto' Padding='0,0,6,0'>
                        <StackPanel>

                            <!-- 1. Clean Start Menu -->
                            <Border x:Name='CardStartMenu' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#172554' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE80F;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#60A5FA' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Clean Start Menu (Pinned Apps Only)' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillStartMenu' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblStartMenu' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Removes All Apps, Categories, Recently Added &amp; Recommended sections (includes Win11 24H2 Education policy hook).'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetStartMenu' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglStartMenu' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 2. Disable Bing / Web Search -->
                            <Border x:Name='CardWebSearch' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#1E293B' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE721;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#38BDF8' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Disable Bing &amp; Web Search in Start Menu' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillWebSearch' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblWebSearch' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Makes Start Menu search instant and strictly local by disabling Bing web results and search box suggestions.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetWebSearch' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglWebSearch' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 3. Taskbar Cleanup -->
                            <Border x:Name='CardTaskbar' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#172554' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE75B;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#60A5FA' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Taskbar Cleanup' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillTaskbar' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblTaskbar' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Hides Search box, Task View, Widgets &amp; Chat icons from the Windows 11 taskbar.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <StackPanel Orientation='Horizontal' Margin='0,4,0,0'>
                                            <CheckBox x:Name='ChkTaskbarLeft' Content='Also align taskbar icons to the left' FontSize='11' Foreground='#94A3B8' Cursor='Hand'/>
                                        </StackPanel>
                                        <TextBlock x:Name='DetTaskbar' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglTaskbar' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 4. End Task in Taskbar -->
                            <Border x:Name='CardEndTask' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#3B1928' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE711;' FontFamily='Segoe MDL2 Assets' FontSize='15' Foreground='#FB7185' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Enable &quot;End Task&quot; in Taskbar Right-Click' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillEndTask' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblEndTask' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Adds an instant End Task button when right-clicking any app on the taskbar (no Task Manager needed).'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetEndTask' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglEndTask' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 5. File Explorer Open This PC -->
                            <Border x:Name='CardThisPc' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#1E293B' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE7F4;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#38BDF8' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='File Explorer Opens to &quot;This PC&quot;' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillThisPc' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblThisPc' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Opens new File Explorer windows directly to This PC instead of Home / Quick Access.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetThisPc' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglThisPc' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 6. Hidden Files & Extensions -->
                            <Border x:Name='CardHiddenFiles' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#1E1B4B' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE8B7;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#A78BFA' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Show Hidden Files &amp; File Extensions' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillHiddenFiles' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblHiddenFiles' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Displays hidden files/folders and full file name extensions in File Explorer.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <StackPanel Orientation='Horizontal' Margin='0,4,0,0'>
                                            <CheckBox x:Name='ChkSuperHidden' Content='Also show protected OS files (ShowSuperHidden)' FontSize='11' Foreground='#94A3B8' Cursor='Hand'/>
                                        </StackPanel>
                                        <TextBlock x:Name='DetHiddenFiles' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglHiddenFiles' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 7. Classic Context Menu -->
                            <Border x:Name='CardContextMenu' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#172554' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE700;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#818CF8' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Restore Classic Right-Click Context Menu' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillContextMenu' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblContextMenu' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Restores the full Windows 10 right-click menu immediately without clicking &quot;Show more options&quot;.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetContextMenu' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglContextMenu' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 8. Disable Windows Sounds -->
                            <Border x:Name='CardSounds' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#3B1928' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE74F;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#F472B6' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Disable Windows System &amp; Startup Sounds' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillSounds' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblSounds' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Activates No Sounds scheme, clears all AppEvents sound paths dynamically, and mutes the startup chime.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetSounds' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglSounds' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 9. Disable Mouse Acceleration -->
                            <Border x:Name='CardMouseAccel' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#1E1B4B' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE962;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#C084FC' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Disable Mouse Acceleration' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillMouseAccel' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblMouseAccel' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Turns off Enhance Pointer Precision immediately via Win32 SPI for consistent 1:1 cursor movement.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetMouseAccel' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglMouseAccel' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 10. Disable Sticky Keys -->
                            <Border x:Name='CardStickyKeys' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#2E1F0F' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE765;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#FBBF24' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Disable Sticky Keys &amp; Filter Keys Popups' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillStickyKeys' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblStickyKeys' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Stops the 5x Shift Sticky Keys and 8-second Shift Filter Keys shortcut dialogs and beeps.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetStickyKeys' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglStickyKeys' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 11. Disable Tips & Ads -->
                            <Border x:Name='CardTipsAds' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#3B1928' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE730;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#FB7185' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Disable Windows Tips, Ads &amp; Setup Nags' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillTipsAds' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblTipsAds' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Silences lock screen fun facts, Settings suggestions, advertising ID, and &quot;Finish setting up&quot; prompts.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetTipsAds' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglTipsAds' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 12. PowerShell Execution Policy -->
                            <Border x:Name='CardExecPolicy' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#064E3B' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE756;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#34D399' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Fix PowerShell Execution Policy (RemoteSigned)' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillExecPolicy' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblExecPolicy' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Sets Windows PowerShell 5.1 ExecutionPolicy to RemoteSigned for LocalMachine (x64 &amp; x86) and CurrentUser.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetExecPolicy' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglExecPolicy' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 13. Ultimate Performance Power Plan -->
                            <Border x:Name='CardPowerPlan' Style='{StaticResource TweakCard}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#2E1F0F' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE945;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#F59E0B' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Ultimate Performance Power Plan' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border x:Name='PillPowerPlan' CornerRadius='4' Padding='7,2' Margin='10,0,0,0'>
                                                <TextBlock x:Name='LblPowerPlan' Text='...' FontSize='10' FontWeight='Bold'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Unlocks and activates Microsoft&apos;s hidden Ultimate Performance power scheme via powercfg.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                        <TextBlock x:Name='DetPowerPlan' Text='' FontSize='11' Foreground='#5E6F88' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <CheckBox x:Name='TglPowerPlan' Grid.Column='2' Style='{StaticResource ToggleSwitch}'/>
                                </Grid>
                            </Border>

                            <!-- 14. Chris Titus Tech WinUtil -->
                            <Border x:Name='CardWinUtil' Style='{StaticResource TweakCard}' BorderBrush='#1E3A5F'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='42'/>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Grid.Column='0' Width='36' Height='36' CornerRadius='8' Background='#083344' VerticalAlignment='Center'>
                                        <TextBlock Text='&#xE90F;' FontFamily='Segoe MDL2 Assets' FontSize='16' Foreground='#22D3EE' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                    </Border>
                                    <StackPanel Grid.Column='1' Margin='12,0,14,0'>
                                        <StackPanel Orientation='Horizontal'>
                                            <TextBlock Text='Chris Titus Tech WinUtil' FontSize='13.5' FontWeight='SemiBold'/>
                                            <Border CornerRadius='4' Padding='7,2' Margin='10,0,0,0' Background='#083344'>
                                                <TextBlock Text='ONLINE' FontSize='10' FontWeight='Bold' Foreground='#22D3EE'/>
                                            </Border>
                                        </StackPanel>
                                        <TextBlock Text='Runs christitus.com/win in an elevated PowerShell console with your selected preset.'
                                                   FontSize='11.5' Foreground='#8B9BB4' TextWrapping='Wrap' Margin='0,3,0,0'/>
                                    </StackPanel>
                                    <StackPanel Grid.Column='2' Orientation='Horizontal' VerticalAlignment='Center'>
                                        <ComboBox x:Name='CmbWinUtil' Width='158' SelectedIndex='0' Margin='0,0,8,0' Style='{StaticResource DarkComboBox}'>
                                            <ComboBoxItem Content='Advanced (Headless)'/>
                                            <ComboBoxItem Content='Standard (Headless)'/>
                                            <ComboBoxItem Content='Minimal (Headless)'/>
                                            <ComboBoxItem Content='Open WinUtil GUI'/>
                                        </ComboBox>
                                        <Button x:Name='BtnLaunchWinUtil' Content='Launch' Style='{StaticResource EmeraldBtn}'/>
                                    </StackPanel>
                                </Grid>
                            </Border>

                        </StackPanel>
                    </ScrollViewer>

                    <!-- ACTIVITY LOG -->
                    <Border Grid.Row='3' Margin='0,10,0,0' Background='#090C11' BorderBrush='#1E2636' BorderThickness='1' CornerRadius='8' Padding='10,8'>
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height='Auto'/>
                                <RowDefinition Height='*'/>
                            </Grid.RowDefinitions>
                            <Grid Grid.Row='0' Margin='0,0,0,4'>
                                <TextBlock Text='ACTIVITY LOG' FontSize='10' FontWeight='Bold' Foreground='#475569' VerticalAlignment='Center'/>
                                <Button x:Name='BtnClearLog' Content='Clear' HorizontalAlignment='Right' Background='Transparent'
                                        Foreground='#64748B' BorderThickness='0' FontSize='10.5' Cursor='Hand' Padding='4,0'/>
                            </Grid>
                            <TextBox x:Name='TxtLog' Grid.Row='1' Background='Transparent' Foreground='#CBD5E1'
                                     BorderThickness='0' IsReadOnly='True' TextWrapping='Wrap'
                                     VerticalScrollBarVisibility='Auto' FontFamily='Consolas' FontSize='11.5'/>
                        </Grid>
                    </Border>
                </Grid>
            </Grid>
        </Grid>
    </Border>
</Window>";

            Window win;
            using (StringReader sr = new StringReader(xaml))
            using (XmlReader xr = XmlReader.Create(sr))
            {
                win = (Window)XamlReader.Load(xr);
            }

            ImageSource icon = LoadEmbeddedIcon();
            if (icon != null)
            {
                win.Icon = icon;
                Image titleImg = (Image)win.FindName("ImgTitleIcon");
                if (titleImg != null) titleImg.Source = icon;
            }

            // Titlebar drag & caption controls
            Border titleBar = (Border)win.FindName("TitleBar");
            titleBar.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2)
                    win.WindowState = (win.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                else
                    try { win.DragMove(); } catch { }
            };

            ((Button)win.FindName("BtnMin")).Click += delegate { win.WindowState = WindowState.Minimized; };
            ((Button)win.FindName("BtnMax")).Click += delegate { win.WindowState = (win.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized; };
            ((Button)win.FindName("BtnClose")).Click += delegate { win.Close(); };

            // Header OS & Admin info
            ((TextBlock)win.FindName("TxtOsInfo")).Text = TweakEngine.GetOsSummary();
            if (!TweakEngine.IsAdmin())
            {
                ((Border)win.FindName("BadgeAdmin")).Background = Hex("#78350F");
                TextBlock tb = (TextBlock)win.FindName("TxtAdminBadge");
                tb.Foreground = Hex("#FCD34D");
                tb.Text = "STANDARD USER";
            }

            TextBox txtLog = (TextBox)win.FindName("TxtLog");
            Action<string, string> log = delegate(string msg, string level)
            {
                txtLog.AppendText(string.Format("[{0:HH:mm:ss}] [{1}] {2}\r\n", DateTime.Now, level, msg));
                txtLog.ScrollToEnd();
            };

            ((Button)win.FindName("BtnClearLog")).Click += delegate { txtLog.Clear(); };

            // Restart-Needed Banner
            Border bannerRestart = (Border)win.FindName("BannerRestart");
            TextBlock txtRestartMsg = (TextBlock)win.FindName("TxtRestartMsg");
            Action<string> showRestartBanner = delegate(string msg)
            {
                txtRestartMsg.Text = msg;
                bannerRestart.Visibility = Visibility.Visible;
            };
            ((Button)win.FindName("BtnBannerDismiss")).Click += delegate { bannerRestart.Visibility = Visibility.Collapsed; };

            // Card registry for Category + Live Search filtering
            List<CardMeta> cards = new List<CardMeta>
            {
                new CardMeta { Card = (Border)win.FindName("CardStartMenu"),   Category = "Shell",  SearchText = "clean start menu pinned apps categories recommended recent" },
                new CardMeta { Card = (Border)win.FindName("CardWebSearch"),   Category = "Shell",  SearchText = "disable bing web search start menu suggestions local" },
                new CardMeta { Card = (Border)win.FindName("CardTaskbar"),     Category = "Shell",  SearchText = "taskbar cleanup widgets chat task view search box align left" },
                new CardMeta { Card = (Border)win.FindName("CardEndTask"),     Category = "Shell",  SearchText = "end task taskbar right click kill process developer" },
                new CardMeta { Card = (Border)win.FindName("CardThisPc"),      Category = "Shell",  SearchText = "file explorer open this pc home quick access launchto" },
                new CardMeta { Card = (Border)win.FindName("CardHiddenFiles"), Category = "Shell",  SearchText = "show hidden files extensions protected os superhidden explorer" },
                new CardMeta { Card = (Border)win.FindName("CardContextMenu"), Category = "Shell",  SearchText = "restore classic right click context menu windows 10" },
                new CardMeta { Card = (Border)win.FindName("CardSounds"),      Category = "System", SearchText = "disable windows system startup sounds mute audio beep" },
                new CardMeta { Card = (Border)win.FindName("CardMouseAccel"),  Category = "System", SearchText = "disable mouse acceleration enhance pointer precision raw input" },
                new CardMeta { Card = (Border)win.FindName("CardStickyKeys"),  Category = "System", SearchText = "disable sticky keys filter keys toggle shift popup beep" },
                new CardMeta { Card = (Border)win.FindName("CardTipsAds"),     Category = "System", SearchText = "disable windows tips ads lock screen finish setting up nags suggestions" },
                new CardMeta { Card = (Border)win.FindName("CardExecPolicy"),  Category = "System", SearchText = "fix powershell execution policy remotesigned scripts" },
                new CardMeta { Card = (Border)win.FindName("CardPowerPlan"),   Category = "System", SearchText = "ultimate performance power plan powercfg cpu speed" },
                new CardMeta { Card = (Border)win.FindName("CardWinUtil"),     Category = "Tools",  SearchText = "chris titus tech winutil preset advanced standard minimal gui" }
            };

            TextBox txtSearch = (TextBox)win.FindName("TxtSearch");
            TextBlock txtPlaceholder = (TextBlock)win.FindName("TxtSearchPlaceholder");
            Button btnClearSearch = (Button)win.FindName("BtnClearSearch");

            Action applyFilter = delegate
            {
                string q = (txtSearch.Text ?? "").Trim().ToLowerInvariant();
                txtPlaceholder.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;
                btnClearSearch.Visibility = string.IsNullOrEmpty(q) ? Visibility.Collapsed : Visibility.Visible;

                foreach (CardMeta cm in cards)
                {
                    bool catMatch = (_activeCategory == "All" || cm.Category == _activeCategory);
                    bool textMatch = string.IsNullOrEmpty(q) || cm.SearchText.Contains(q);
                    cm.Card.Visibility = (catMatch && textMatch) ? Visibility.Visible : Visibility.Collapsed;
                }
            };

            txtSearch.TextChanged += delegate { applyFilter(); };
            btnClearSearch.Click += delegate { txtSearch.Text = ""; txtSearch.Focus(); };

            // Sidebar Navigation
            Button navAll = (Button)win.FindName("NavAll");
            Button navShell = (Button)win.FindName("NavShell");
            Button navSystem = (Button)win.FindName("NavSystem");
            Button navTools = (Button)win.FindName("NavTools");

            Action<string> setCategory = delegate(string cat)
            {
                _activeCategory = cat;
                foreach (Button b in new[] { navAll, navShell, navSystem, navTools })
                {
                    b.Background = Brushes.Transparent;
                    b.Foreground = Hex("#94A3B8");
                }
                Button activeBtn = navAll;
                string title = "All Tweaks";
                if (cat == "Shell") { activeBtn = navShell; title = "Shell & Explorer"; }
                else if (cat == "System") { activeBtn = navSystem; title = "System & Performance"; }
                else if (cat == "Tools") { activeBtn = navTools; title = "External Tools"; }

                activeBtn.Background = Hex("#172554");
                activeBtn.Foreground = Hex("#60A5FA");
                ((TextBlock)win.FindName("TxtSectionTitle")).Text = title;
                applyFilter();
            };

            navAll.Click += delegate { setCategory("All"); };
            navShell.Click += delegate { setCategory("Shell"); };
            navSystem.Click += delegate { setCategory("System"); };
            navTools.Click += delegate { setCategory("Tools"); };

            // Pill helper
            Action<string, string, string, string, TweakStatus> applyCardStatus = delegate(string pillName, string lblName, string detName, string tglName, TweakStatus st)
            {
                Border pill = (Border)win.FindName(pillName);
                TextBlock lbl = (TextBlock)win.FindName(lblName);
                TextBlock det = (TextBlock)win.FindName(detName);
                CheckBox tgl = (CheckBox)win.FindName(tglName);

                det.Text = st.Detail;
                if (st.State == TweakState.Applied)
                {
                    pill.Background = Hex("#064E3B");
                    lbl.Foreground = Hex("#6EE7B7");
                    lbl.Text = "ACTIVE";
                    tgl.IsChecked = true;
                }
                else if (st.State == TweakState.Partial)
                {
                    pill.Background = Hex("#78350F");
                    lbl.Foreground = Hex("#FCD34D");
                    lbl.Text = "PARTIAL";
                    tgl.IsChecked = false;
                }
                else
                {
                    pill.Background = Hex("#1E293B");
                    lbl.Foreground = Hex("#94A3B8");
                    lbl.Text = "OFF";
                    tgl.IsChecked = false;
                }
            };

            Action refreshAll = delegate
            {
                _suppressToggleEvents = true;
                try
                {
                    TweakStatus s1 = TweakEngine.GetStartMenuStatus();
                    TweakStatus s2 = TweakEngine.GetWebSearchStatus();
                    TweakStatus s3 = TweakEngine.GetTaskbarCleanupStatus();
                    TweakStatus s4 = TweakEngine.GetEndTaskStatus();
                    TweakStatus s5 = TweakEngine.GetExplorerThisPcStatus();
                    TweakStatus s6 = TweakEngine.GetHiddenFilesStatus();
                    TweakStatus s7 = TweakEngine.GetContextMenuStatus();
                    TweakStatus s8 = TweakEngine.GetSoundsStatus();
                    TweakStatus s9 = TweakEngine.GetMouseAccelStatus();
                    TweakStatus s10 = TweakEngine.GetStickyKeysStatus();
                    TweakStatus s11 = TweakEngine.GetTipsAdsStatus();
                    TweakStatus s12 = TweakEngine.GetExecPolicyStatus();
                    TweakStatus s13 = TweakEngine.GetPowerPlanStatus();

                    applyCardStatus("PillStartMenu", "LblStartMenu", "DetStartMenu", "TglStartMenu", s1);
                    applyCardStatus("PillWebSearch", "LblWebSearch", "DetWebSearch", "TglWebSearch", s2);
                    applyCardStatus("PillTaskbar", "LblTaskbar", "DetTaskbar", "TglTaskbar", s3);
                    ((CheckBox)win.FindName("ChkTaskbarLeft")).IsChecked = s3.ExtraFlag;
                    applyCardStatus("PillEndTask", "LblEndTask", "DetEndTask", "TglEndTask", s4);
                    applyCardStatus("PillThisPc", "LblThisPc", "DetThisPc", "TglThisPc", s5);
                    applyCardStatus("PillHiddenFiles", "LblHiddenFiles", "DetHiddenFiles", "TglHiddenFiles", s6);
                    ((CheckBox)win.FindName("ChkSuperHidden")).IsChecked = s6.ExtraFlag;
                    applyCardStatus("PillContextMenu", "LblContextMenu", "DetContextMenu", "TglContextMenu", s7);
                    applyCardStatus("PillSounds", "LblSounds", "DetSounds", "TglSounds", s8);
                    applyCardStatus("PillMouseAccel", "LblMouseAccel", "DetMouseAccel", "TglMouseAccel", s9);
                    applyCardStatus("PillStickyKeys", "LblStickyKeys", "DetStickyKeys", "TglStickyKeys", s10);
                    applyCardStatus("PillTipsAds", "LblTipsAds", "DetTipsAds", "TglTipsAds", s11);
                    applyCardStatus("PillExecPolicy", "LblExecPolicy", "DetExecPolicy", "TglExecPolicy", s12);
                    applyCardStatus("PillPowerPlan", "LblPowerPlan", "DetPowerPlan", "TglPowerPlan", s13);

                    TweakStatus[] all = new[] { s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13 };
                    int active = 0;
                    foreach (TweakStatus ts in all) if (ts.State == TweakState.Applied) active++;

                    ((TextBlock)win.FindName("TxtActiveRatio")).Text = string.Format("{0} / {1}", active, all.Length);
                    ((Border)win.FindName("BarProgress")).Width = Math.Round((180.0 * active) / all.Length);
                }
                finally
                {
                    _suppressToggleEvents = false;
                }
            };

            Action<string, Action, string> runSafe = delegate(string label, Action act, string restartNote)
            {
                try
                {
                    act();
                    log(label + " succeeded.", "OK");
                    if (!string.IsNullOrEmpty(restartNote)) showRestartBanner(restartNote);
                }
                catch (Exception ex)
                {
                    log(label + " failed: " + ex.Message, "ERROR");
                }
                refreshAll();
            };

            // Toggle Handlers
            CheckBox tglStart = (CheckBox)win.FindName("TglStartMenu");
            tglStart.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglStart.IsChecked == true)
                    runSafe("Enable Clean Start Menu", TweakEngine.ApplyStartMenu, "Restart Explorer & Start Menu (or sign out/in once) to hide Recommended completely.");
                else
                    runSafe("Revert Start Menu", TweakEngine.RevertStartMenu, "Restart Explorer & Start Menu to restore default Start Menu.");
            };

            CheckBox tglWebSearch = (CheckBox)win.FindName("TglWebSearch");
            tglWebSearch.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglWebSearch.IsChecked == true)
                    runSafe("Disable Bing Web Search", TweakEngine.ApplyWebSearch, "Restart Explorer & Start Menu to apply local-only Start search.");
                else
                    runSafe("Revert Bing Web Search", TweakEngine.RevertWebSearch, "Restart Explorer & Start Menu to restore web search.");
            };

            CheckBox tglTaskbar = (CheckBox)win.FindName("TglTaskbar");
            CheckBox chkTbLeft = (CheckBox)win.FindName("ChkTaskbarLeft");
            tglTaskbar.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglTaskbar.IsChecked == true)
                    runSafe("Apply Taskbar Cleanup", delegate { TweakEngine.ApplyTaskbarCleanup(chkTbLeft.IsChecked == true); }, null);
                else
                    runSafe("Revert Taskbar Cleanup", TweakEngine.RevertTaskbarCleanup, null);
            };
            chkTbLeft.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglTaskbar.IsChecked == true)
                    runSafe("Update Taskbar Alignment", delegate { TweakEngine.ApplyTaskbarCleanup(chkTbLeft.IsChecked == true); }, null);
            };

            CheckBox tglEndTask = (CheckBox)win.FindName("TglEndTask");
            tglEndTask.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglEndTask.IsChecked == true)
                    runSafe("Enable End Task in Taskbar", TweakEngine.ApplyEndTask, null);
                else
                    runSafe("Disable End Task in Taskbar", TweakEngine.RevertEndTask, null);
            };

            CheckBox tglThisPc = (CheckBox)win.FindName("TglThisPc");
            tglThisPc.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglThisPc.IsChecked == true)
                    runSafe("Set File Explorer to This PC", TweakEngine.ApplyExplorerThisPc, null);
                else
                    runSafe("Revert File Explorer to Home", TweakEngine.RevertExplorerThisPc, null);
            };

            CheckBox tglHidden = (CheckBox)win.FindName("TglHiddenFiles");
            CheckBox chkSuper = (CheckBox)win.FindName("ChkSuperHidden");
            tglHidden.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglHidden.IsChecked == true)
                    runSafe("Show Hidden Files & Extensions", delegate { TweakEngine.ApplyHiddenFiles(chkSuper.IsChecked == true); }, "Restart Explorer if open File Explorer windows do not refresh automatically.");
                else
                    runSafe("Revert Hidden Files & Extensions", TweakEngine.RevertHiddenFiles, null);
            };
            chkSuper.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglHidden.IsChecked == true)
                    runSafe("Update Protected OS Files visibility", delegate { TweakEngine.ApplyHiddenFiles(chkSuper.IsChecked == true); }, null);
            };

            CheckBox tglCtx = (CheckBox)win.FindName("TglContextMenu");
            tglCtx.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglCtx.IsChecked == true)
                    runSafe("Restore Classic Context Menu", TweakEngine.ApplyContextMenu, "Restart Explorer to activate the classic right-click context menu.");
                else
                    runSafe("Revert to Windows 11 Context Menu", TweakEngine.RevertContextMenu, "Restart Explorer to restore the Windows 11 context menu.");
            };

            CheckBox tglSounds = (CheckBox)win.FindName("TglSounds");
            tglSounds.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglSounds.IsChecked == true)
                    runSafe("Disable Windows & Startup Sounds", TweakEngine.ApplySounds, "Sign out and back in for cached system sounds to mute completely.");
                else
                    runSafe("Restore Windows Sounds", TweakEngine.RevertSounds, null);
            };

            CheckBox tglMouse = (CheckBox)win.FindName("TglMouseAccel");
            tglMouse.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglMouse.IsChecked == true)
                    runSafe("Disable Mouse Acceleration", TweakEngine.ApplyMouseAccel, null);
                else
                    runSafe("Restore Mouse Acceleration", TweakEngine.RevertMouseAccel, null);
            };

            CheckBox tglSticky = (CheckBox)win.FindName("TglStickyKeys");
            tglSticky.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglSticky.IsChecked == true)
                    runSafe("Disable Sticky & Filter Keys Popups", TweakEngine.ApplyStickyKeys, null);
                else
                    runSafe("Restore Sticky Keys Defaults", TweakEngine.RevertStickyKeys, null);
            };

            CheckBox tglTips = (CheckBox)win.FindName("TglTipsAds");
            tglTips.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglTips.IsChecked == true)
                    runSafe("Disable Windows Tips, Ads & Setup Nags", TweakEngine.ApplyTipsAds, null);
                else
                    runSafe("Restore Windows Tips & Suggestions", TweakEngine.RevertTipsAds, null);
            };

            CheckBox tglExec = (CheckBox)win.FindName("TglExecPolicy");
            tglExec.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglExec.IsChecked == true)
                    runSafe("Set PowerShell ExecutionPolicy (RemoteSigned)", TweakEngine.ApplyExecPolicy, null);
                else
                    runSafe("Revert PowerShell ExecutionPolicy", TweakEngine.RevertExecPolicy, null);
            };

            CheckBox tglPower = (CheckBox)win.FindName("TglPowerPlan");
            tglPower.Click += delegate
            {
                if (_suppressToggleEvents) return;
                if (tglPower.IsChecked == true)
                    runSafe("Activate Ultimate Performance Power Plan", TweakEngine.ApplyUltimatePowerPlan, null);
                else
                    runSafe("Switch Power Plan to Balanced", TweakEngine.RevertUltimatePowerPlan, null);
            };

            // WinUtil Launcher
            ((Button)win.FindName("BtnLaunchWinUtil")).Click += delegate
            {
                ComboBox cmb = (ComboBox)win.FindName("CmbWinUtil");
                int idx = cmb.SelectedIndex;
                string presetArg = "";
                if (idx == 0) presetArg = "-Preset Advanced -Run";
                else if (idx == 1) presetArg = "-Preset Standard -Run";
                else if (idx == 2) presetArg = "-Preset Minimal -Run";

                string psCmd =
                    "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
                    "try { $s = Invoke-RestMethod 'https://christitus.com/win'; & ([ScriptBlock]::Create($s)) " + presetArg + " } " +
                    "catch { Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red }; " +
                    "Write-Host ''; Read-Host 'Press Enter to close'";

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + psCmd + "\"");
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    log("Launched Chris Titus WinUtil (" + ((ComboBoxItem)cmb.SelectedItem).Content + ").", "OK");
                }
                catch (Exception ex)
                {
                    log("Failed to launch WinUtil: " + ex.Message, "ERROR");
                }
            };

            // System Restore Point (async)
            Button btnRestore = (Button)win.FindName("BtnRestorePoint");
            btnRestore.Click += delegate
            {
                btnRestore.IsEnabled = false;
                btnRestore.Content = "Creating Restore Point...";
                log("Creating System Restore Point 'WindowsTweaks Backup'...", "INFO");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        TweakEngine.CreateRestorePoint();
                        win.Dispatcher.Invoke(new Action(delegate
                        {
                            log("System Restore Point created successfully!", "OK");
                            btnRestore.Content = "Create Restore Point";
                            btnRestore.IsEnabled = true;
                        }));
                    }
                    catch (Exception ex)
                    {
                        win.Dispatcher.Invoke(new Action(delegate
                        {
                            log("Restore Point: " + ex.Message, "WARN");
                            btnRestore.Content = "Create Restore Point";
                            btnRestore.IsEnabled = true;
                        }));
                    }
                });
            };

            // Export / Import JSON Profile
            ((Button)win.FindName("BtnExport")).Click += delegate
            {
                SaveFileDialog sfd = new SaveFileDialog();
                sfd.Title = "Export Windows Tweaks Profile";
                sfd.Filter = "JSON Profile (*.json)|*.json";
                sfd.FileName = "WindowsTweaks-Profile.json";
                if (sfd.ShowDialog(win) == true)
                {
                    try
                    {
                        TweakProfile prof = new TweakProfile
                        {
                            Version = 1,
                            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            CleanStartMenu = (tglStart.IsChecked == true),
                            DisableWebSearch = (tglWebSearch.IsChecked == true),
                            TaskbarCleanup = (tglTaskbar.IsChecked == true),
                            AlignTaskbarLeft = (chkTbLeft.IsChecked == true),
                            TaskbarEndTask = (tglEndTask.IsChecked == true),
                            ExplorerThisPc = (tglThisPc.IsChecked == true),
                            ShowHiddenFiles = (tglHidden.IsChecked == true),
                            ShowSuperHidden = (chkSuper.IsChecked == true),
                            ClassicContextMenu = (tglCtx.IsChecked == true),
                            DisableSounds = (tglSounds.IsChecked == true),
                            DisableMouseAccel = (tglMouse.IsChecked == true),
                            DisableStickyKeys = (tglSticky.IsChecked == true),
                            DisableTipsAndAds = (tglTips.IsChecked == true),
                            PowerShellExecPolicy = (tglExec.IsChecked == true),
                            UltimatePowerPlan = (tglPower.IsChecked == true)
                        };
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        File.WriteAllText(sfd.FileName, ser.Serialize(prof));
                        log("Exported profile to " + Path.GetFileName(sfd.FileName), "OK");
                    }
                    catch (Exception ex)
                    {
                        log("Export failed: " + ex.Message, "ERROR");
                    }
                }
            };

            ((Button)win.FindName("BtnImport")).Click += delegate
            {
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Title = "Import Windows Tweaks Profile";
                ofd.Filter = "JSON Profile (*.json)|*.json";
                if (ofd.ShowDialog(win) == true)
                {
                    try
                    {
                        string json = File.ReadAllText(ofd.FileName);
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        TweakProfile p = ser.Deserialize<TweakProfile>(json);
                        if (p == null) throw new InvalidOperationException("Invalid profile JSON.");

                        if (p.CleanStartMenu) TweakEngine.ApplyStartMenu(); else TweakEngine.RevertStartMenu();
                        if (p.DisableWebSearch) TweakEngine.ApplyWebSearch(); else TweakEngine.RevertWebSearch();
                        if (p.TaskbarCleanup) TweakEngine.ApplyTaskbarCleanup(p.AlignTaskbarLeft); else TweakEngine.RevertTaskbarCleanup();
                        if (p.TaskbarEndTask) TweakEngine.ApplyEndTask(); else TweakEngine.RevertEndTask();
                        if (p.ExplorerThisPc) TweakEngine.ApplyExplorerThisPc(); else TweakEngine.RevertExplorerThisPc();
                        if (p.ShowHiddenFiles) TweakEngine.ApplyHiddenFiles(p.ShowSuperHidden); else TweakEngine.RevertHiddenFiles();
                        if (p.ClassicContextMenu) TweakEngine.ApplyContextMenu(); else TweakEngine.RevertContextMenu();
                        if (p.DisableSounds) TweakEngine.ApplySounds(); else TweakEngine.RevertSounds();
                        if (p.DisableMouseAccel) TweakEngine.ApplyMouseAccel(); else TweakEngine.RevertMouseAccel();
                        if (p.DisableStickyKeys) TweakEngine.ApplyStickyKeys(); else TweakEngine.RevertStickyKeys();
                        if (p.DisableTipsAndAds) TweakEngine.ApplyTipsAds(); else TweakEngine.RevertTipsAds();
                        if (p.PowerShellExecPolicy) TweakEngine.ApplyExecPolicy(); else TweakEngine.RevertExecPolicy();
                        if (p.UltimatePowerPlan) TweakEngine.ApplyUltimatePowerPlan(); else TweakEngine.RevertUltimatePowerPlan();

                        refreshAll();
                        log("Imported & applied profile: " + Path.GetFileName(ofd.FileName), "OK");
                        showRestartBanner("Profile imported! Restart Explorer & Start Menu to apply all shell changes.");
                    }
                    catch (Exception ex)
                    {
                        log("Import failed: " + ex.Message, "ERROR");
                    }
                }
            };

            // Quick Actions
            Action doRestartExplorer = delegate
            {
                runSafe("Restart Explorer & Start Menu", TweakEngine.RestartExplorer, null);
                bannerRestart.Visibility = Visibility.Collapsed;
            };

            ((Button)win.FindName("BtnRestartExplorer")).Click += delegate { doRestartExplorer(); };
            ((Button)win.FindName("BtnBannerRestart")).Click += delegate { doRestartExplorer(); };

            ((Button)win.FindName("BtnApplyAll")).Click += delegate
            {
                runSafe("Apply All 13 Tweaks", delegate
                {
                    TweakEngine.ApplyStartMenu();
                    TweakEngine.ApplyWebSearch();
                    TweakEngine.ApplyTaskbarCleanup(chkTbLeft.IsChecked == true);
                    TweakEngine.ApplyEndTask();
                    TweakEngine.ApplyExplorerThisPc();
                    TweakEngine.ApplyHiddenFiles(chkSuper.IsChecked == true);
                    TweakEngine.ApplyContextMenu();
                    TweakEngine.ApplySounds();
                    TweakEngine.ApplyMouseAccel();
                    TweakEngine.ApplyStickyKeys();
                    TweakEngine.ApplyTipsAds();
                    TweakEngine.ApplyExecPolicy();
                    TweakEngine.ApplyUltimatePowerPlan();
                }, "All 13 tweaks applied! Click 'Restart Explorer Now' (and sign out/in once for Start Menu).");
            };

            ((Button)win.FindName("BtnRevertAll")).Click += delegate
            {
                MessageBoxResult confirm = MessageBox.Show(
                    "Revert all 13 tweaks back to Windows defaults?",
                    "Confirm Revert All",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                runSafe("Revert All 13 Tweaks", delegate
                {
                    TweakEngine.RevertStartMenu();
                    TweakEngine.RevertWebSearch();
                    TweakEngine.RevertTaskbarCleanup();
                    TweakEngine.RevertEndTask();
                    TweakEngine.RevertExplorerThisPc();
                    TweakEngine.RevertHiddenFiles();
                    TweakEngine.RevertContextMenu();
                    TweakEngine.RevertSounds();
                    TweakEngine.RevertMouseAccel();
                    TweakEngine.RevertStickyKeys();
                    TweakEngine.RevertTipsAds();
                    TweakEngine.RevertExecPolicy();
                    TweakEngine.RevertUltimatePowerPlan();
                }, "All 13 tweaks reverted! Restart Explorer to restore default shell UI.");
            };

            ((Button)win.FindName("BtnRefresh")).Click += delegate
            {
                refreshAll();
                log("Live registry status refreshed.", "INFO");
            };

            setCategory("All");
            refreshAll();
            log("Ready. 13 tweaks + WinUtil loaded.", "INFO");

            return win;
        }
    }
}
