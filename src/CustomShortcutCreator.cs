using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WindowsTweaksApp
{
    internal static class CustomShortcutCreator
    {
        internal const string TargetIcon = "Use target icon";

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--open")
                {
                    OpenTarget(args[1]);
                    return;
                }

                if (args.Length == 0 || (args.Length <= 2 && args[0] == "--edit"))
                {
                    OpenEditor(args.Length == 2 ? args[1] : null);
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private static void OpenEditor(string path)
        {
            new Application().Run(new ShortcutEditor(path));
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "Custom Shortcut Creator", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static void OpenTarget(string target)
        {
            ProcessStartInfo start;
            if (Directory.Exists(target))
            {
                // Launch Explorer explicitly. ShellExecute can report success for a folder without opening a window.
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                start = new ProcessStartInfo(explorer, ShortcutIntegration.QuoteArgument(target));
                start.UseShellExecute = false;
            }
            else
            {
                if (!File.Exists(target)) throw new IOException("The shortcut target could not be found. " + target);
                start = new ProcessStartInfo(target);
                start.UseShellExecute = true;
                start.WorkingDirectory = Path.GetDirectoryName(target);
            }
            using (Process process = Process.Start(start)) { }
        }

        internal static ShortcutResult Create(string name, string target, string icon, string destination, Func<bool> confirmUpdate)
        {
            name = ValidateName(name);
            target = FullPath(target, "Enter a target path.");
            if (!Directory.Exists(target) && !File.Exists(target))
                throw new IOException("The target file or folder could not be found.");
            destination = FullPath(destination, "Choose an export folder.");
            if (!Directory.Exists(destination)) throw new IOException("The export folder could not be found.");

            string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ShortcutIntegration.StorageName);
            string start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Windows Tweaks Shortcuts");
            string quickAccess = Path.Combine(storage, "Quick Access");
            string fileName = name + ".lnk";
            string exportLink = Path.Combine(destination, fileName);
            string startLink = Path.Combine(start, fileName);
            string quickFolder = Path.Combine(quickAccess, name);
            bool sameLink = string.Equals(exportLink, startLink, StringComparison.OrdinalIgnoreCase);
            string id = Guid.NewGuid().ToString("N");
            string iconFolder = Path.Combine(storage, "Icons");
            string iconFile = Path.Combine(iconFolder, id + ".ico");
            string stagedFolder = Path.Combine(quickAccess, "." + id);
            string stagedIcon = Path.Combine(iconFolder, id + ".tmp");
            string stagedStart = Path.Combine(start, id + ".tmp");
            string stagedExport = Path.Combine(destination, id + ".tmp");

            if (File.Exists(startLink))
            {
                return UpdateExisting(target, icon, exportLink, startLink, quickFolder, iconFolder, confirmUpdate);
            }

            CheckAvailable(exportLink, "A shortcut with this name already exists in the export folder.");
            CheckAvailable(startLink, "A shortcut with this name already exists in Start. Choose a different name.");
            CheckAvailable(quickFolder, "A Quick Access folder already has this name. Choose a different name.");

            bool movedIcon = false, movedStart = false, movedExport = false, movedFolder = false;
            try
            {
                Directory.CreateDirectory(iconFolder);
                Directory.CreateDirectory(start);
                Directory.CreateDirectory(stagedFolder);

                string folderIcon = Path.Combine(stagedFolder, "Shortcut.ico");
                ShortcutShell.SaveIcon(target, icon, folderIcon);
                File.SetAttributes(folderIcon, FileAttributes.Hidden);
                File.Copy(folderIcon, stagedIcon);
                File.SetAttributes(stagedIcon, FileAttributes.Normal);

                string folderIni = Path.Combine(stagedFolder, "desktop.ini");
                File.WriteAllText(folderIni, "[.ShellClassInfo]\r\nIconFile=Shortcut.ico\r\nIconIndex=0\r\n", Encoding.Unicode);
                File.SetAttributes(folderIni, FileAttributes.Hidden | FileAttributes.System);

                string innerLink = Path.Combine(stagedFolder, fileName);
                string helper = Path.Combine(storage, ShortcutIntegration.HelperName);
                File.Move(stagedIcon, iconFile);
                movedIcon = true;
                ShortcutShell.WriteLink(innerLink, helper, target, iconFile, id);
                File.Copy(innerLink, stagedStart);
                if (!sameLink) File.Copy(innerLink, stagedExport);

                File.Move(stagedStart, startLink);
                movedStart = true;
                if (!sameLink)
                {
                    File.Move(stagedExport, exportLink);
                    movedExport = true;
                }
                Directory.Move(stagedFolder, quickFolder);
                movedFolder = true;
                ShortcutShell.MarkCustomizedFolder(quickFolder);
            }
            catch
            {
                if (movedFolder) TryCleanup(delegate { DeleteStagingFolder(quickFolder, fileName); });
                if (movedExport) TryCleanup(delegate { File.Delete(exportLink); });
                if (movedStart) TryCleanup(delegate { File.Delete(startLink); });
                if (movedIcon) TryCleanup(delegate { File.Delete(iconFile); });
                throw;
            }
            finally
            {
                TryCleanup(delegate { File.Delete(stagedStart); });
                TryCleanup(delegate { File.Delete(stagedExport); });
                TryCleanup(delegate { File.Delete(stagedIcon); });
                TryCleanup(delegate { DeleteStagingFolder(stagedFolder, fileName); });
            }

            ShortcutShell.NotifyCreated(exportLink, startLink, quickFolder);
            return new ShortcutResult(exportLink, startLink, quickFolder);
        }

        private static ShortcutResult UpdateExisting(string target, string icon, string exportLink, string startLink,
            string quickFolder, string iconFolder, Func<bool> confirmUpdate)
        {
            string helper = Path.Combine(Path.GetDirectoryName(iconFolder), ShortcutIntegration.HelperName);
            string storedIcon = ShortcutShell.GetOwnedIconPath(startLink, helper, target, iconFolder);
            if (storedIcon == null)
                throw new IOException("A different shortcut already uses this name in Start. Choose a different name.");

            string nestedLink = Path.Combine(quickFolder, Path.GetFileName(startLink));
            string folderIcon = Path.Combine(quickFolder, "Shortcut.ico");
            if (!File.Exists(storedIcon) || !File.Exists(folderIcon) ||
                !SamePath(ShortcutShell.GetOwnedIconPath(nestedLink, helper, target, iconFolder), storedIcon))
                throw new IOException("The existing shortcut’s icon or Quick Access files are missing or changed. Choose a different name.");

            bool exportExists = File.Exists(exportLink);
            if (Directory.Exists(exportLink) || (exportExists &&
                !SamePath(ShortcutShell.GetOwnedIconPath(exportLink, helper, target, iconFolder), storedIcon)))
                throw new IOException("A different item already uses this name in the export folder. Choose a different name or export folder.");

            if (!confirmUpdate()) return null;

            using (FileStream updateLock = new FileStream(storedIcon + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                return ReplaceExistingIcon(target, icon, exportLink, startLink, nestedLink, quickFolder, storedIcon,
                    folderIcon, exportExists);
            }
        }

        private static ShortcutResult ReplaceExistingIcon(string target, string icon, string exportLink, string startLink,
            string nestedLink, string quickFolder, string storedIcon, string folderIcon, bool exportExists)
        {
            string updateId = Guid.NewGuid().ToString("N");
            string suffix = "." + updateId;
            string stagedIcon = storedIcon + suffix + ".tmp";
            string stagedFolderIcon = folderIcon + suffix + ".tmp";
            string stagedExport = Path.Combine(Path.GetDirectoryName(exportLink), updateId + ".tmp");
            string iconBackup = storedIcon + suffix + ".bak";
            string folderBackup = folderIcon + suffix + ".bak";
            bool updatedIcon = false, updatedFolder = false;
            try
            {
                ShortcutShell.SaveIcon(target, icon, stagedIcon);
                File.Copy(stagedIcon, stagedFolderIcon);
                File.SetAttributes(stagedFolderIcon, FileAttributes.Hidden);
                if (!exportExists) File.Copy(startLink, stagedExport);

                File.Replace(stagedIcon, storedIcon, iconBackup);
                updatedIcon = true;
                File.Replace(stagedFolderIcon, folderIcon, folderBackup);
                updatedFolder = true;
                if (!exportExists) File.Move(stagedExport, exportLink);
            }
            catch
            {
                if (updatedFolder) TryCleanup(delegate { File.Replace(folderBackup, folderIcon, null); });
                if (updatedIcon) TryCleanup(delegate { File.Replace(iconBackup, storedIcon, null); });
                throw;
            }
            finally
            {
                TryCleanup(delegate { File.Delete(stagedIcon); });
                TryCleanup(delegate { File.Delete(stagedFolderIcon); });
                TryCleanup(delegate { File.Delete(stagedExport); });
            }

            TryCleanup(delegate { File.Delete(iconBackup); });
            TryCleanup(delegate { File.Delete(folderBackup); });
            ShortcutShell.NotifyUpdated(exportLink, startLink, nestedLink, quickFolder, storedIcon, !exportExists);
            return new ShortcutResult(exportLink, startLink, quickFolder, true);
        }

        private static bool SamePath(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static string ValidateName(string name)
        {
            name = (name ?? "").Trim();
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            if (name.Length == 0 || name.EndsWith(".") || name.EndsWith(" ") ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(name.Split('.')[0].TrimEnd(),
                    @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException("Enter a valid Windows shortcut name.");
            return name;
        }

        private static string FullPath(string path, string message)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(message);
            string value = path.Trim().Trim('"');
            if (!Path.IsPathRooted(value) || value.Length < 3 ||
                !(value[1] == ':' && (value[2] == '\\' || value[2] == '/') || value.StartsWith(@"\\", StringComparison.Ordinal)))
                throw new ArgumentException("Enter a full Windows path.");
            return Path.GetFullPath(value);
        }

        private static void CheckAvailable(string path, string message)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException(message);
        }

        private static void DeleteStagingFolder(string path, string linkName)
        {
            if (!Directory.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            foreach (string name in new[] { "desktop.ini", "Shortcut.ico", linkName })
            {
                string file = Path.Combine(path, name);
                if (!File.Exists(file)) continue;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            Directory.Delete(path);
        }

        private static void TryCleanup(Action action)
        {
            try { action(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class ShortcutResult
    {
        internal readonly string ExportLink;
        internal readonly string StartLink;
        internal readonly string QuickAccessFolder;
        internal readonly bool Updated;

        internal ShortcutResult(string exportLink, string startLink, string quickAccessFolder, bool updated = false)
        {
            ExportLink = exportLink;
            StartLink = startLink;
            QuickAccessFolder = quickAccessFolder;
            Updated = updated;
        }
    }

    internal sealed class ShortcutEditor : Window
    {
        private readonly StackPanel content;
        private readonly TextBox name, target, icon, destination;
        private readonly Image iconPreview;
        private readonly TextBlock error;

        internal ShortcutEditor(string selectedPath)
        {
            Title = "Custom Shortcut Creator";
            Width = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(15, 20, 29));
            Foreground = new SolidColorBrush(Color.FromRgb(230, 237, 247));
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            content = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
            Content = content;
            content.Children.Add(new TextBlock { Text = "Create custom shortcut", FontSize = 21, FontWeight = FontWeights.SemiBold });

            name = AddField("Name");
            target = AddField("Path");
            AddFieldButton(target, "File…", delegate { BrowseFile(target, "Choose a target file", "All files|*.*"); });
            AddFieldButton(target, "Folder…", delegate { BrowseFolder(target, "Choose a target folder"); });
            icon = AddField("Icon");
            iconPreview = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(iconPreview, BitmapScalingMode.HighQuality);
            ((DockPanel)icon.Parent).Children.Insert(0, iconPreview);
            DockPanel.SetDock(iconPreview, Dock.Left);
            AddFieldButton(icon, "Browse…", delegate { BrowseFile(icon, "Choose an icon", "Windows icons (*.ico)|*.ico"); });
            AddFieldButton(icon, "Reset", delegate { icon.Text = CustomShortcutCreator.TargetIcon; UpdatePreview(); });
            destination = AddField("Export to");
            AddFieldButton(destination, "Browse…", delegate { BrowseFolder(destination, "Choose where to export the shortcut"); });

            error = new TextBlock { Foreground = Brushes.LightSalmon, TextWrapping = TextWrapping.Wrap };
            content.Children.Add(error);
            StackPanel actions = AddActions();
            Button cancel = AddButton(actions, "Cancel", Close);
            cancel.IsCancel = true;
            Button create = AddButton(actions, "Create", CreateShortcut);
            create.IsDefault = true;
            create.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            create.Foreground = Brushes.White;

            target.Text = selectedPath ?? "";
            if (!string.IsNullOrEmpty(selectedPath)) name.Text = new DirectoryInfo(selectedPath).Name;
            icon.Text = CustomShortcutCreator.TargetIcon;
            destination.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            target.LostKeyboardFocus += delegate { UpdatePreview(); };
            icon.LostKeyboardFocus += delegate { UpdatePreview(); };
            Loaded += delegate { UpdatePreview(); name.Focus(); name.SelectAll(); };
        }

        private TextBox AddField(string label)
        {
            TextBox box = new TextBox
            {
                MinWidth = 190, Padding = new Thickness(7, 5, 7, 5),
                Background = new SolidColorBrush(Color.FromRgb(23, 32, 46)), Foreground = Brushes.White,
                CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(59, 75, 96))
            };
            Label caption = new Label { Content = label, Target = box, Foreground = Foreground, Padding = new Thickness(0, 10, 0, 4) };
            content.Children.Add(caption);
            DockPanel field = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            field.Children.Add(box);
            content.Children.Add(field);
            return box;
        }

        private static void AddFieldButton(TextBox box, string label, Action action)
        {
            DockPanel field = (DockPanel)box.Parent;
            Button button = MakeButton(label, action);
            DockPanel.SetDock(button, Dock.Right);
            field.Children.Insert(field.Children.IndexOf(box), button);
        }

        private StackPanel AddActions()
        {
            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            content.Children.Add(actions);
            return actions;
        }

        private static Button MakeButton(string label, Action action)
        {
            Button button = new Button { Content = label, Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(6, 0, 0, 0) };
            button.Click += delegate { action(); };
            return button;
        }

        private static Button AddButton(Panel panel, string label, Action action)
        {
            Button button = MakeButton(label, action);
            panel.Children.Add(button);
            return button;
        }

        private void BrowseFile(TextBox box, string title, string filter)
        {
            OpenFileDialog dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            box.Text = dialog.FileName;
            FillEmptyName();
            UpdatePreview();
        }

        private void BrowseFolder(TextBox box, string title)
        {
            using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = title;
                dialog.SelectedPath = Directory.Exists(box.Text) ? box.Text : "";
                if (dialog.ShowDialog(new FormsOwner(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK) return;
                box.Text = dialog.SelectedPath;
            }
            FillEmptyName();
            UpdatePreview();
        }

        private void FillEmptyName()
        {
            if (!string.IsNullOrWhiteSpace(name.Text)) return;
            if (Directory.Exists(target.Text)) name.Text = new DirectoryInfo(target.Text).Name;
            else if (File.Exists(target.Text)) name.Text = Path.GetFileNameWithoutExtension(target.Text);
        }

        private void UpdatePreview()
        {
            try { iconPreview.Source = ShortcutShell.GetIconPreview(target.Text, icon.Text); }
            catch (Exception) { iconPreview.Source = null; }
        }

        private void CreateShortcut()
        {
            try
            {
                error.Text = "";
                ShortcutResult result = CustomShortcutCreator.Create(name.Text, target.Text, icon.Text, destination.Text,
                    delegate
                    {
                        return MessageBox.Show(this,
                            "This shortcut already exists. Update its icon and make it available in the selected export folder?",
                            "Update custom shortcut", MessageBoxButton.YesNo, MessageBoxImage.Question,
                            MessageBoxResult.No) == MessageBoxResult.Yes;
                    });
                if (result != null) ShowResult(result);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
            }
        }

        private void ShowResult(ShortcutResult result)
        {
            content.Children.Clear();
            content.Children.Add(new TextBlock
            {
                Text = result.Updated ? "Shortcut updated" : "Shortcut created",
                FontSize = 21, FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock
            {
                Text = "Saved to " + result.ExportLink + "\n\nRight-click the Start shortcut to pin it to Start. " +
                    "To pin it to the taskbar, search Start for its name and use Pin to taskbar. " +
                    "To pin the Quick Access folder, show it and right-click the folder in Explorer.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6)
            });
            StackPanel actions = AddActions();
            AddButton(actions, "Show exported shortcut", delegate { Reveal(result.ExportLink); });
            AddButton(actions, "Show Start shortcut", delegate { Reveal(result.StartLink); });
            AddButton(actions, "Show Quick Access folder", delegate { Reveal(result.QuickAccessFolder); });
            Button done = AddButton(actions, "Done", Close);
            done.IsDefault = true;
            done.IsCancel = true;
            done.Focus();
        }

        private void Reveal(string path)
        {
            try { ShortcutShell.SelectInExplorer(path); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private sealed class FormsOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            internal FormsOwner(IntPtr handle) { Handle = handle; }
        }
    }

    internal static class ShortcutShell
    {
        private static readonly Guid ShellLink = new Guid("00021401-0000-0000-C000-000000000046");
        private const string ShortcutAppIdPrefix = "projectGloria.WindowsTweaks.Shortcut.";
        private const ushort VtLpWStr = 31;

        private static PropertyKey AppIdKey()
        {
            return new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        }

        internal static string GetOwnedIconPath(string path, string helper, string target, string iconFolder)
        {
            if (!File.Exists(path)) return null;
            object linkObject = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLink));
            try
            {
                ((IPersistFile)linkObject).Load(path, 0);
                IShellLinkW link = (IShellLinkW)linkObject;
                StringBuilder buffer = new StringBuilder(4096);
                link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
                if (!string.Equals(buffer.ToString(), helper, StringComparison.OrdinalIgnoreCase)) return null;
                buffer.Clear();
                link.GetArguments(buffer, buffer.Capacity);
                if (!string.Equals(buffer.ToString(), "--open " + ShortcutIntegration.QuoteArgument(target),
                    StringComparison.OrdinalIgnoreCase)) return null;

                PropertyKey key = AppIdKey();
                PropVariant value;
                ((IPropertyStore)linkObject).GetValue(ref key, out value);
                string appId;
                try { appId = value.Type == VtLpWStr ? Marshal.PtrToStringUni(value.Data) : null; }
                finally { PropVariantClear(ref value); }
                if (appId == null || !appId.StartsWith(ShortcutAppIdPrefix, StringComparison.Ordinal)) return null;
                Guid id;
                if (!Guid.TryParseExact(appId.Substring(ShortcutAppIdPrefix.Length), "N", out id)) return null;

                int iconIndex;
                buffer.Clear();
                link.GetIconLocation(buffer, buffer.Capacity, out iconIndex);
                string expected = Path.Combine(iconFolder, id.ToString("N") + ".ico");
                return iconIndex == 0 && string.Equals(buffer.ToString(), expected, StringComparison.OrdinalIgnoreCase)
                    ? expected : null;
            }
            catch (COMException) { return null; }
            finally { Marshal.FinalReleaseComObject(linkObject); }
        }

        internal static void WriteLink(string path, string helper, string target, string icon, string appId)
        {
            object linkObject = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLink));
            try
            {
                IShellLinkW link = (IShellLinkW)linkObject;
                link.SetPath(helper);
                link.SetArguments("--open " + ShortcutIntegration.QuoteArgument(target));
                link.SetWorkingDirectory(Path.GetDirectoryName(helper));
                link.SetIconLocation(icon, 0);
                link.SetDescription(target);
                IPropertyStore properties = (IPropertyStore)linkObject;
                PropertyKey key = AppIdKey();
                PropVariant value = new PropVariant
                {
                    Type = VtLpWStr,
                    Data = Marshal.StringToCoTaskMemUni(ShortcutAppIdPrefix + appId)
                };
                try
                {
                    properties.SetValue(ref key, ref value);
                    properties.Commit();
                }
                finally { PropVariantClear(ref value); }
                ((IPersistFile)linkObject).Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(linkObject); }
        }

        internal static BitmapSource GetIconPreview(string target, string icon)
        {
            if (!UsesTargetIcon(icon))
            {
                using (FileStream stream = File.OpenRead(IconPath(icon)))
                {
                    BitmapFrame best = null;
                    foreach (BitmapFrame frame in new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad).Frames)
                    {
                        if (best == null || frame.PixelWidth > best.PixelWidth) best = frame;
                    }
                    return best;
                }
            }
            int? index = GetSystemIconIndex(target);
            if (!index.HasValue) return null;
            return ReadSystemIcon(index.Value, ExtraLargeIcons) ?? ReadSystemIcon(index.Value, LargeIcons);
        }

        private const int LargeIcons = 0;
        private const int SmallIcons = 1;
        private const int ExtraLargeIcons = 2;
        private const int JumboIcons = 4;
        private static readonly Guid ImageListId = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

        private static int? GetSystemIconIndex(string target)
        {
            if (!File.Exists(target) && !Directory.Exists(target)) return null;
            ShellFileInfo info;
            const uint SystemIconIndex = 0x4000;
            if (SHGetFileInfo(target, 0, out info, Marshal.SizeOf(typeof(ShellFileInfo)), SystemIconIndex) == IntPtr.Zero)
                return null;
            return info.IconIndex;
        }

        private static BitmapSource ReadSystemIcon(int index, int size)
        {
            Guid id = ImageListId;
            IImageList list;
            if (SHGetImageList(size, ref id, out list) < 0 || list == null) return null;
            try
            {
                IntPtr icon;
                const uint Transparent = 0x1;
                if (list.GetIcon(index, Transparent, out icon) < 0 || icon == IntPtr.Zero) return null;
                try
                {
                    BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return bitmap;
                }
                finally { DestroyIcon(icon); }
            }
            finally { Marshal.ReleaseComObject(list); }
        }

        internal static void SaveIcon(string target, string icon, string path)
        {
            if (!UsesTargetIcon(icon))
            {
                if (GetIconPreview(target, icon) == null) throw new IOException("Windows could not read the selected icon.");
                File.Copy(IconPath(icon), path);
                return;
            }

            int? index = GetSystemIconIndex(target);
            if (!index.HasValue) throw new IOException("Windows could not load the target icon. Choose a custom icon.");

            List<BitmapSource> frames = new List<BitmapSource>();
            HashSet<int> widths = new HashSet<int>();
            foreach (int size in new[] { SmallIcons, LargeIcons, ExtraLargeIcons, JumboIcons })
            {
                BitmapSource bitmap = ReadSystemIcon(index.Value, size);
                if (bitmap != null && widths.Add(bitmap.PixelWidth)) frames.Add(bitmap);
            }
            if (frames.Count == 0) throw new IOException("Windows could not load the target icon. Choose a custom icon.");

            WriteIconFrames(path, frames);
        }

        private static void WriteIconFrames(string path, List<BitmapSource> frames)
        {
            List<byte[]> images = new List<byte[]>();
            foreach (BitmapSource frame in frames)
            {
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using (MemoryStream png = new MemoryStream())
                {
                    encoder.Save(png);
                    images.Add(png.ToArray());
                }
            }

            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.CreateNew)))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)frames.Count);
                int offset = 6 + 16 * frames.Count;
                for (int i = 0; i < frames.Count; i++)
                {
                    BitmapSource frame = frames[i];
                    // An ICO directory records the 256-pixel dimension as zero.
                    writer.Write(frame.PixelWidth == 256 ? (byte)0 : (byte)frame.PixelWidth);
                    writer.Write(frame.PixelHeight == 256 ? (byte)0 : (byte)frame.PixelHeight);
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) writer.Write(image);
            }
        }

        private static bool UsesTargetIcon(string icon)
        {
            return string.IsNullOrWhiteSpace(icon) || string.Equals(icon.Trim(), CustomShortcutCreator.TargetIcon, StringComparison.OrdinalIgnoreCase);
        }

        private static string IconPath(string icon)
        {
            string path = icon.Trim().Trim('"');
            if (!string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a Windows icon file (.ico).");
            return path;
        }

        internal static void MarkCustomizedFolder(string path)
        {
            if (!PathMakeSystemFolder(path)) throw new IOException("Windows could not apply the Quick Access folder icon.");
        }

        internal static void NotifyCreated(string exportLink, string startLink, string quickFolder)
        {
            const uint Create = 0x00000002, CreateDirectory = 0x00000008, UnicodePath = 0x0005;
            SHChangeNotify(Create, UnicodePath, exportLink, IntPtr.Zero);
            if (!string.Equals(exportLink, startLink, StringComparison.OrdinalIgnoreCase))
                SHChangeNotify(Create, UnicodePath, startLink, IntPtr.Zero);
            SHChangeNotify(CreateDirectory, UnicodePath, quickFolder, IntPtr.Zero);
        }

        internal static void NotifyUpdated(string exportLink, string startLink, string nestedLink, string quickFolder,
            string storedIcon, bool newExport)
        {
            const uint Create = 0x00000002, UpdateItem = 0x00002000, UnicodePath = 0x0005;
            foreach (string path in new[] { startLink, nestedLink, quickFolder, storedIcon })
                SHChangeNotify(UpdateItem, UnicodePath, path, IntPtr.Zero);
            if (!string.Equals(exportLink, startLink, StringComparison.OrdinalIgnoreCase))
                SHChangeNotify(newExport ? Create : UpdateItem, UnicodePath, exportLink, IntPtr.Zero);
        }

        internal static void SelectInExplorer(string path)
        {
            IntPtr parent, item;
            uint attributes;
            Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetDirectoryName(path), IntPtr.Zero, out parent, 0, out attributes));
            try
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out item, 0, out attributes));
                try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(parent, 1, new[] { ILFindLastID(item) }, 0)); }
                finally { Marshal.FreeCoTaskMem(item); }
            }
            finally { Marshal.FreeCoTaskMem(parent); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid Format;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort Type;
            private ushort reserved1, reserved2, reserved3;
            public IntPtr Data;
            private IntPtr padding;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellFileInfo
        {
            internal IntPtr Icon;
            internal int IconIndex;
            internal uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        private interface IImageList
        {
            void Add(IntPtr image, IntPtr mask, out int index);
            void ReplaceIcon(int index, IntPtr icon, out int newIndex);
            void SetOverlayImage(int image, int overlay);
            void Replace(int index, IntPtr image, IntPtr mask);
            void AddMasked(IntPtr image, uint maskColor, out int index);
            void Draw(IntPtr parameters);
            void Remove(int index);
            [PreserveSig] int GetIcon(int index, uint flags, out IntPtr icon);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant variant);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, int size, uint flags);
        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int size, ref Guid id, out IImageList list);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool PathMakeSystemFolder(string path);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint evt, uint flags, string path, IntPtr item2);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string path, IntPtr binding, out IntPtr pidl, uint mask, out uint attributes);
        [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, [In] IntPtr[] items, uint flags);
    }
}
