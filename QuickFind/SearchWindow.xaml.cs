using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickFind.Core;
using QuickFind.Helpers;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Clipboard = System.Windows.Clipboard;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Color = System.Windows.Media.Color;

namespace QuickFind;

public partial class SearchWindow : Window
{
    private readonly FileIndex _index;
    private readonly SearchEngine _searchEngine;
    private readonly DispatcherTimer _debounceTimer;
    private string _lastQuery = "";
    private bool _forceClose;
    private string? _selectedDrive; // null = all drives

    // Icon cache to avoid repeated shell calls. Bounded to keep memory
    // predictable: extensions are usually ~50-200 distinct values, but a
    // pathological user with every filetype under the sun shouldn't grow
    // this unbounded. On overflow we evict the oldest insertion.
    private const int IconCacheMaxEntries = 512;
    private static readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> _iconCacheOrder = new();
    private static readonly object _iconCacheLock = new();

    public SearchWindow(FileIndex index)
    {
        InitializeComponent();
        _index = index;
        _searchEngine = new SearchEngine(index);

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _debounceTimer.Tick += DebounceTimer_Tick;

        UpdateStatus();
    }

    public void UpdateStatus(string? message = null)
    {
        if (message != null)
        {
            StatusText.Text = message;
            return;
        }

        var lastIndex = IndexPersistence.GetLastIndexDate();
        string lastStr = lastIndex.HasValue
            ? $"Last indexed: {lastIndex.Value:g}"
            : "Not indexed";
        StatusText.Text = $"{_index.Count:N0} files indexed  |  {lastStr}";

        // Refresh drive buttons when index changes
        PopulateDriveButtons();
    }

    public void ShowAndFocus()
    {
        // WPF's focus model is unreliable the instant a window becomes
        // visible: Show() → Activate() fires before window activation
        // actually completes, so a synchronous SearchBox.Focus() often
        // lands on the window chrome instead. Queuing on the dispatcher
        // at Input priority means the focus call runs after the OS-level
        // activation, which matches what the user expects ("Alt+Space,
        // start typing").
        Show();
        Activate();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    // ── Search ─────────────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async void DebounceTimer_Tick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        string query = SearchBox.Text.Trim();
        if (query == _lastQuery) return;
        _lastQuery = query;

        if (query.Length == 0)
        {
            ResultsList.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateText.Text = "Start typing to search...\nTip: use >100MB or >1GB to find large files";
            UpdateStatus();
            return;
        }

        bool searchContents = ContentSearchCheck.IsChecked == true;

        try
        {
            var results = await _searchEngine.SearchAsync(query, searchContents);
            var items = results.Select(CreateResultItem).ToList();
            ResultsList.ItemsSource = items;
            StatusText.Text = $"{items.Count} results  |  {_index.Count:N0} files indexed";

            if (items.Count > 0)
            {
                EmptyState.Visibility = Visibility.Collapsed;
                ResultsList.SelectedIndex = 0;
            }
            else
            {
                EmptyState.Visibility = Visibility.Visible;
                EmptyStateText.Text = "No results found";
            }
        }
        catch (OperationCanceledException) { }
    }

    private ResultItem CreateResultItem(SearchEngine.SearchResult result)
    {
        string directory;
        try { directory = _index.ResolveDirectory(result.EntryIndex); }
        catch { directory = "?"; }

        string sizeText = "";
        if (!result.IsDirectory)
        {
            long size = result.Size;
            if (size <= 0)
            {
                try
                {
                    string fullPath = Path.Combine(directory, result.Name);
                    var fi = new FileInfo(fullPath);
                    if (fi.Exists) size = fi.Length;
                }
                catch { }
            }
            if (size > 0) sizeText = FormatSize(size);
        }

        string nameWithoutExt = result.IsDirectory
            ? result.Name
            : Path.GetFileNameWithoutExtension(result.Name);
        string ext = result.IsDirectory ? "" : Path.GetExtension(result.Name);

        var icon = GetCachedFileIcon(result.Name, result.IsDirectory);
        var iconBg = result.IsDirectory
            ? new SolidColorBrush(Color.FromArgb(40, 217, 119, 6))
            : new SolidColorBrush(Color.FromArgb(25, 37, 99, 235));

        string typeBadge = GetTypeBadge(ext, result.IsDirectory);
        var typeBadgeBg = GetTypeBadgeColor(ext, result.IsDirectory);
        var typeBadgeVis = string.IsNullOrEmpty(typeBadge) ? Visibility.Collapsed : Visibility.Visible;

        string shortDir = ShortenPath(directory, 55);

        return new ResultItem(
            nameWithoutExt, ext, directory, shortDir, sizeText,
            result.EntryIndex, result.IsDirectory,
            icon, iconBg, typeBadge, typeBadgeBg, typeBadgeVis);
    }

    // ── Icon helpers ───────────────────────────────────────────────────

    private static ImageSource? GetCachedFileIcon(string fileName, bool isDirectory)
    {
        string cacheKey = isDirectory ? ":folder:" : Path.GetExtension(fileName).ToLowerInvariant();
        if (cacheKey == "") cacheKey = ":noext:";

        lock (_iconCacheLock)
        {
            if (_iconCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var icon = ExtractIcon(fileName, isDirectory);

        lock (_iconCacheLock)
        {
            // Another thread may have already added it — keep the first entry.
            if (!_iconCache.ContainsKey(cacheKey))
            {
                _iconCache[cacheKey] = icon;
                _iconCacheOrder.Enqueue(cacheKey);
                while (_iconCache.Count > IconCacheMaxEntries && _iconCacheOrder.Count > 0)
                {
                    string evict = _iconCacheOrder.Dequeue();
                    _iconCache.Remove(evict);
                }
            }
        }
        return icon;
    }

    private static ImageSource? ExtractIcon(string fileName, bool isDirectory)
    {
        try
        {
            var shfi = new NativeMethods.SHFILEINFO();
            uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON | NativeMethods.SHGFI_USEFILEATTRIBUTES;
            uint fileAttrs = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : 0u;

            NativeMethods.SHGetFileInfo(
                isDirectory ? "folder" : fileName,
                fileAttrs, ref shfi,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);

            if (shfi.hIcon == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            NativeMethods.DestroyIcon(shfi.hIcon);
            return source;
        }
        catch { return null; }
    }

    // ── Formatting helpers ─────────────────────────────────────────────

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static string ShortenPath(string path, int maxLen)
    {
        if (path.Length <= maxLen) return path;
        // Keep drive + ... + last folder(s)
        int driveEnd = path.IndexOf('\\');
        if (driveEnd < 0) return path[..(maxLen - 3)] + "...";
        string drive = path[..(driveEnd + 1)];
        string rest = path[(driveEnd + 1)..];
        int available = maxLen - drive.Length - 4; // 4 for "...\\"
        if (available <= 0) return path[..(maxLen - 3)] + "...";
        return drive + "...\\" + rest[^Math.Min(available, rest.Length)..];
    }

    private static string GetTypeBadge(string ext, bool isDir)
    {
        if (isDir) return "FOLDER";
        return ext.ToUpperInvariant() switch
        {
            ".EXE" or ".MSI" => "APP",
            ".DLL" => "DLL",
            ".PDF" => "PDF",
            ".DOC" or ".DOCX" => "DOC",
            ".XLS" or ".XLSX" => "XLS",
            ".PPT" or ".PPTX" => "PPT",
            ".ZIP" or ".RAR" or ".7Z" or ".TAR" or ".GZ" => "ZIP",
            ".PNG" or ".JPG" or ".JPEG" or ".GIF" or ".BMP" or ".SVG" or ".WEBP" or ".ICO" => "IMG",
            ".MP4" or ".MKV" or ".AVI" or ".MOV" or ".WMV" => "VID",
            ".MP3" or ".WAV" or ".FLAC" or ".OGG" or ".AAC" => "AUD",
            ".CS" or ".JS" or ".TS" or ".PY" or ".JAVA" or ".CPP" or ".C" or ".H" or ".GO" or ".RS" => "CODE",
            ".JSON" or ".XML" or ".YAML" or ".YML" or ".TOML" => "CFG",
            ".MD" or ".TXT" or ".LOG" => "TXT",
            ".HTML" or ".CSS" or ".SCSS" => "WEB",
            ".SLN" or ".CSPROJ" or ".VBPROJ" => "PROJ",
            _ => ""
        };
    }

    private static SolidColorBrush GetTypeBadgeColor(string ext, bool isDir)
    {
        if (isDir) return new SolidColorBrush(Color.FromArgb(220, 180, 100, 0));
        string badge = GetTypeBadge(ext, false);
        return badge switch
        {
            "APP" => new SolidColorBrush(Color.FromArgb(220, 185, 28, 28)),
            "CODE" => new SolidColorBrush(Color.FromArgb(220, 16, 130, 58)),
            "IMG" => new SolidColorBrush(Color.FromArgb(220, 100, 45, 195)),
            "VID" => new SolidColorBrush(Color.FromArgb(220, 180, 30, 100)),
            "AUD" => new SolidColorBrush(Color.FromArgb(220, 195, 70, 8)),
            "PDF" or "DOC" or "XLS" or "PPT" => new SolidColorBrush(Color.FromArgb(220, 30, 80, 200)),
            "ZIP" => new SolidColorBrush(Color.FromArgb(220, 140, 85, 5)),
            "WEB" => new SolidColorBrush(Color.FromArgb(220, 10, 95, 120)),
            "CFG" or "PROJ" => new SolidColorBrush(Color.FromArgb(200, 85, 90, 105)),
            "TXT" => new SolidColorBrush(Color.FromArgb(200, 85, 90, 105)),
            _ => new SolidColorBrush(Color.FromArgb(180, 130, 135, 145))
        };
    }

    // ── Keyboard ───────────────────────────────────────────────────────

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ResultsList.Items.Count > 0)
                {
                    ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex);
                    ResultsList.Focus();
                    var container = (System.Windows.Controls.ListBoxItem?)
                        ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex);
                    container?.Focus();
                }
                e.Handled = true;
                break;

            case Key.Enter:
                if (ResultsList.SelectedItem is ResultItem sel)
                    OpenResult(sel, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;

            case Key.Escape:
                HideWindow();
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (ResultsList.SelectedItem is ResultItem sel)
                    OpenResult(sel, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;

            case Key.Delete:
                if (ResultsList.SelectedItem is ResultItem delSel && !delSel.IsDirectory)
                {
                    string fullPath = Path.Combine(delSel.Directory, delSel.Name + delSel.Extension);
                    DeleteToRecycleBin(fullPath, delSel.EntryIndex);
                }
                e.Handled = true;
                break;

            case Key.Escape:
                SearchBox.Focus();
                e.Handled = true;
                break;

            case Key.Back:
            case Key.Left:
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is ResultItem sel)
            OpenResult(sel, false);
    }

    private void ContentSearchCheck_Changed(object sender, RoutedEventArgs e)
    {
        _lastQuery = "";
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    // ── Right-click context menu ───────────────────────────────────────

    private void ResultsList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultItem item) return;

        string fullPath = Path.Combine(item.Directory, item.Name + item.Extension);

        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var sepStyle = (Style)FindResource("DarkMenuSeparator");
        var miStyle = (Style)FindResource("DarkMenuItem");

        void AddItem(string icon, string header, string? gesture, Action action)
        {
            var mi = new MenuItem { Header = header, Tag = icon, Style = miStyle };
            if (gesture != null) mi.InputGestureText = gesture;
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        void AddSep() => menu.Items.Add(new Separator { Style = sepStyle });

        if (item.IsDirectory)
        {
            AddItem("\uD83D\uDCC2", "Open Folder", "Enter", () => OpenResult(item, false));
            AddItem("\uD83D\uDCBB", "Open in Terminal", "Ctrl+T", () => OpenInTerminal(fullPath, true));
        }
        else
        {
            AddItem("\uD83D\uDCC4", "Open File", "Enter", () => OpenResult(item, false));
            AddItem("\uD83D\uDCC2", "Open Containing Folder", "Ctrl+Enter", () => OpenResult(item, true));
            AddSep();
            AddItem("\uD83D\uDCBB", "Open Folder in Terminal", "Ctrl+T", () => OpenInTerminal(item.Directory, false));
            AddItem("\u270F\uFE0F", "Open in VS Code", null, () => OpenInVSCode(fullPath));
        }

        AddSep();
        AddItem("\uD83D\uDCCB", "Copy Full Path", "Ctrl+C", () => CopyToClipboard(fullPath));
        AddItem("\uD83D\uDCCB", "Copy File Name", null, () => CopyToClipboard(item.Name + item.Extension));
        AddItem("\uD83D\uDCCB", "Copy Folder Path", null, () => CopyToClipboard(item.Directory));

        AddSep();
        AddItem("\u2139\uFE0F", "Properties", null, () => ShowProperties(fullPath));

        if (!item.IsDirectory)
        {
            AddItem("\uD83D\uDDD1\uFE0F", "Delete to Recycle Bin", "Del", () => DeleteToRecycleBin(fullPath, item.EntryIndex));
        }

        menu.IsOpen = true;
    }

    // ── Actions ────────────────────────────────────────────────────────

    // Extensions we refuse to launch without an explicit user confirmation.
    // These are all direct-execution vectors: a mis-click in the search
    // results shouldn't silently run untrusted code.
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".scr", ".com", ".cpl", ".msc", ".reg", ".pif", ".hta"
    };

    private void OpenResult(ResultItem item, bool openFolder)
    {
        try
        {
            string fullPath = Path.Combine(item.Directory, item.Name + item.Extension);

            if (item.IsDirectory && !openFolder)
            {
                Process.Start("explorer.exe", $"\"{fullPath}\"");
            }
            else if (openFolder)
            {
                Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
            }
            else
            {
                // Confirm before running executables / scripts.
                if (DangerousExtensions.Contains(item.Extension))
                {
                    var confirm = System.Windows.MessageBox.Show(
                        this,
                        $"Run this {item.Extension.TrimStart('.').ToUpperInvariant()} file?\n\n{fullPath}",
                        "QuickFind — Confirm launch",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning,
                        MessageBoxResult.No);
                    if (confirm != MessageBoxResult.Yes) return;
                }
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }

            HideWindow();
        }
        catch (Exception ex)
        {
            Core.Logger.Warn($"OpenResult failed for {item.Name}{item.Extension}", ex);
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private static void OpenInTerminal(string path, bool isFolder)
    {
        try
        {
            string dir = isFolder ? path : Path.GetDirectoryName(path) ?? path;
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{dir}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback to cmd if Windows Terminal not available
            try
            {
                string dir = isFolder ? path : Path.GetDirectoryName(path) ?? path;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private static void OpenInVSCode(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
                CreateNoWindow = true
            });
        }
        catch { }
    }

    private static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { }
    }

    private static void ShowProperties(string path)
    {
        try
        {
            NativeMethods.ShowFileProperties(path);
        }
        catch { }
    }

    private void DeleteToRecycleBin(string path, int entryIndex = -1)
    {
        try
        {
            var result = System.Windows.MessageBox.Show(
                $"Move to Recycle Bin?\n\n{Path.GetFileName(path)}",
                "QuickFind", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                NativeMethods.MoveToRecycleBin(path);

                // Tombstone immediately so the next search doesn't keep
                // showing the deleted file. The USN watcher will arrive at
                // the same conclusion a few seconds later; this is just
                // the low-latency user-facing path.
                if (entryIndex >= 0)
                    _index.RemoveByEntryIndex(entryIndex);

                RefreshResults();
            }
        }
        catch (Exception ex)
        {
            Core.Logger.Warn($"DeleteToRecycleBin failed for {path}", ex);
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    // Force the next debounce tick to run a fresh search even if the query
    // text hasn't changed. Used after a delete or any other operation that
    // mutates the underlying index.
    private void RefreshResults()
    {
        _lastQuery = "\u0000";
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    // ── Window buttons ───────────────────────────────────────────────

    public event Action? ExitRequested;

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideWindow();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // ── Drive filter buttons ────────────────────────────────────────────

    public void PopulateDriveButtons()
    {
        DriveButtonsPanel.Children.Clear();

        // "All" button
        var allBtn = CreateDriveButton("All", null);
        DriveButtonsPanel.Children.Add(allBtn);

        // Per-drive buttons
        foreach (var drive in _index.GetIndexedDrives())
        {
            string label = drive.TrimEnd('\\');
            var btn = CreateDriveButton(label, drive);
            DriveButtonsPanel.Children.Add(btn);
        }

        UpdateDriveButtonStyles();
    }

    private System.Windows.Controls.Button CreateDriveButton(string label, string? driveRoot)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = label,
            Tag = driveRoot,
            Style = (Style)FindResource("DriveFilterButton")
        };
        btn.Click += DriveFilter_Click;
        return btn;
    }

    private void DriveFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn)
        {
            _selectedDrive = btn.Tag as string;
            _searchEngine.DriveFilter = _selectedDrive;
            UpdateDriveButtonStyles();

            // Re-trigger search with new filter
            _lastQuery = "";
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    private void UpdateDriveButtonStyles()
    {
        foreach (var child in DriveButtonsPanel.Children)
        {
            if (child is System.Windows.Controls.Button btn)
            {
                bool isActive = (btn.Tag as string) == _selectedDrive;
                if (isActive)
                {
                    btn.Background = (SolidColorBrush)FindResource("SelectedBrush");
                    btn.BorderBrush = (SolidColorBrush)FindResource("AccentBrush");
                    btn.Foreground = (SolidColorBrush)FindResource("AccentBrush");
                }
                else
                {
                    btn.Background = (SolidColorBrush)FindResource("SurfaceBrush");
                    btn.BorderBrush = (SolidColorBrush)FindResource("BorderBrush");
                    btn.Foreground = (SolidColorBrush)FindResource("TextBrush");
                }
            }
        }
    }

    // ── Size filter buttons ─────────────────────────────────────────────

    private void SizeFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string filter)
        {
            SearchBox.Text = filter;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            SearchBox.Focus();
        }
    }

    // ── Window behavior ────────────────────────────────────────────────

    private void HideWindow() => Hide();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            HideWindow();
        }
    }
}

// ── Data models ────────────────────────────────────────────────────────

public record ResultItem(
    string Name,
    string Extension,
    string Directory,
    string ShortDirectory,
    string SizeText,
    int EntryIndex,
    bool IsDirectory,
    ImageSource? Icon,
    SolidColorBrush IconBackground,
    string TypeBadge,
    SolidColorBrush TypeBadgeBackground,
    Visibility TypeBadgeVisibility);

// ── Converters ─────────────────────────────────────────────────────────

public class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int len && len == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
