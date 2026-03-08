using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NHotkey;
using NHotkey.Wpf;
using QuickFind.Core;
using QuickFind.Helpers;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace QuickFind;

public partial class App : Application
{
    private static Mutex? _mutex;
    private WinForms.NotifyIcon? _notifyIcon;
    private SearchWindow? _searchWindow;
    private FileIndex _index = new();
    private CancellationTokenSource _indexCts = new();

    private const string TaskbarSearchKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search";
    private const string TaskbarSearchValue = "SearchboxTaskbarMode";
    private const string ScheduledTaskName = "QuickFindAdmin";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance check
        _mutex = new Mutex(true, "QuickFind_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("QuickFind is already running.", "QuickFind",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Global error handler
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Error: {args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "QuickFind Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        SetupTrayIcon();
        RegisterHotkey();

        _searchWindow = new SearchWindow(_index);
        _searchWindow.ExitRequested += ExitApp;

        // Show search window on first launch
        _searchWindow.ShowAndFocus();

        // Start indexing
        _ = IndexAllDrivesAsync();
    }

    // ── Tray Icon ──────────────────────────────────────────────────────

    private void SetupTrayIcon()
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "QuickFind — Alt+Space to search",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();

        var showItem = menu.Items.Add("Search  (Alt+Space)");
        showItem.Click += (_, _) => Dispatcher.Invoke(ToggleSearch);
        showItem.Font = new Font(showItem.Font, System.Drawing.FontStyle.Bold);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var reindexItem = menu.Items.Add("Re-index");
        reindexItem.Click += (_, _) => Dispatcher.Invoke(() => _ = ReindexAsync());

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // Replace Windows Search
        var replaceSearchItem = new WinForms.ToolStripMenuItem("Replace Windows Search");
        replaceSearchItem.ToolTipText = "Hide the Windows search box/icon and use QuickFind instead";
        replaceSearchItem.Checked = IsWindowsSearchHidden();
        replaceSearchItem.Click += (_, _) => Dispatcher.Invoke(() => ToggleReplaceWindowsSearch(replaceSearchItem));
        menu.Items.Add(replaceSearchItem);

        // Auto-start as Admin (via Task Scheduler - no UAC)
        var autoAdminItem = new WinForms.ToolStripMenuItem("Start with Windows (Admin, no UAC)");
        autoAdminItem.ToolTipText = "Uses Task Scheduler to launch as Admin without UAC prompt";
        autoAdminItem.Checked = IsScheduledTaskInstalled();
        autoAdminItem.Click += (_, _) => Dispatcher.Invoke(() => ToggleScheduledTask(autoAdminItem));
        menu.Items.Add(autoAdminItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // Status
        string mode = IsElevated() ? "Running as Admin (MFT)" : "Running as User (standard)";
        var statusItem = menu.Items.Add(mode);
        statusItem.Enabled = false;

        if (!IsElevated())
        {
            var elevateItem = menu.Items.Add("Restart as Admin now...");
            elevateItem.Click += (_, _) => RestartElevated();
        }

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = menu.Items.Add("Exit");
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitApp);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleSearch);
    }

    private static Icon LoadAppIcon()
    {
        string icoPath = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
        if (File.Exists(icoPath))
            return new Icon(icoPath);

        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("QuickFind.Resources.app.ico");
        if (stream != null)
            return new Icon(stream);

        return CreateFallbackIcon();
    }

    private static Icon CreateFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.Clear(Color.Transparent);

        // Blue rounded background
        using var bgBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
        using var bgPath = new System.Drawing.Drawing2D.GraphicsPath();
        float r = 7f;
        bgPath.AddArc(2, 2, r * 2, r * 2, 180, 90);
        bgPath.AddArc(30 - r * 2, 2, r * 2, r * 2, 270, 90);
        bgPath.AddArc(30 - r * 2, 30 - r * 2, r * 2, r * 2, 0, 90);
        bgPath.AddArc(2, 30 - r * 2, r * 2, r * 2, 90, 90);
        bgPath.CloseFigure();
        g.FillPath(bgBrush, bgPath);

        // White magnifying glass
        using var pen = new Pen(Color.White, 2.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawEllipse(pen, 9, 8, 12, 12);
        g.DrawLine(new Pen(Color.White, 3f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }, 19, 18, 24, 23);

        return Icon.FromHandle(bmp.GetHicon());
    }

    // ── Hotkey ─────────────────────────────────────────────────────────

    private void RegisterHotkey()
    {
        try
        {
            HotkeyManager.Current.AddOrReplace("ShowSearch",
                Key.Space, ModifierKeys.Alt, OnHotkeyPressed);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not register Alt+Space hotkey: {ex.Message}\n\n" +
                "Another application may be using this shortcut.",
                "QuickFind", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        ToggleSearch();
        e.Handled = true;
    }

    private void ToggleSearch()
    {
        if (_searchWindow == null) return;
        if (_searchWindow.IsVisible)
            _searchWindow.Hide();
        else
            _searchWindow.ShowAndFocus();
    }

    // ── Task Scheduler (Admin without UAC) ─────────────────────────────

    private static bool IsScheduledTaskInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{ScheduledTaskName}\" /fo LIST",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private void ToggleScheduledTask(WinForms.ToolStripMenuItem menuItem)
    {
        try
        {
            if (IsScheduledTaskInstalled())
            {
                // Remove scheduled task
                RunSchtasks($"/delete /tn \"{ScheduledTaskName}\" /f");
                menuItem.Checked = false;

                // Also remove old registry auto-start if present
                RemoveRegistryAutoStart();

                ShowBalloon("Auto-start removed", "QuickFind will no longer start with Windows.");
            }
            else
            {
                // Create scheduled task: runs at logon, as admin, no UAC
                string exePath = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exePath))
                {
                    MessageBox.Show("Could not determine EXE path.", "QuickFind",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string xml = CreateScheduledTaskXml(exePath);
                string xmlPath = Path.Combine(Path.GetTempPath(), "QuickFind_task.xml");
                File.WriteAllText(xmlPath, xml);

                bool ok = RunSchtasks($"/create /tn \"{ScheduledTaskName}\" /xml \"{xmlPath}\" /f");
                File.Delete(xmlPath);

                if (ok)
                {
                    menuItem.Checked = true;
                    // Remove old registry auto-start to avoid double launch
                    RemoveRegistryAutoStart();
                    ShowBalloon("Auto-start enabled",
                        "QuickFind will start with Windows as Admin — no UAC prompt.");
                }
                else
                {
                    MessageBox.Show(
                        "Failed to create scheduled task.\nMake sure you're running as Admin for the first setup.",
                        "QuickFind", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "QuickFind",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string CreateScheduledTaskXml(string exePath)
    {
        string username = $"{Environment.UserDomainName}\\{Environment.UserName}";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>QuickFind - Fast file search (runs as Admin without UAC)</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{username}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{username}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions>
                <Exec>
                  <Command>{exePath}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void RestartElevated()
    {
        // If scheduled task exists, use it (no UAC)
        if (IsScheduledTaskInstalled())
        {
            RunSchtasks($"/run /tn \"{ScheduledTaskName}\"");
            Current.Dispatcher.Invoke(() => Current.Shutdown());
            return;
        }

        // Otherwise, use runas (will show UAC once)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Verb = "runas",
                UseShellExecute = true
            };
            Process.Start(psi);
            Current.Dispatcher.Invoke(() => Current.Shutdown());
        }
        catch { /* user cancelled UAC */ }
    }

    // ── Replace Windows Search ─────────────────────────────────────────

    private static bool IsWindowsSearchHidden()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TaskbarSearchKey, false);
            var val = key?.GetValue(TaskbarSearchValue);
            return val is int mode && mode == 0;
        }
        catch { return false; }
    }

    private void ToggleReplaceWindowsSearch(WinForms.ToolStripMenuItem menuItem)
    {
        try
        {
            bool currentlyHidden = IsWindowsSearchHidden();
            if (currentlyHidden)
            {
                SetWindowsSearchMode(1);
                menuItem.Checked = false;
                ShowBalloon("Windows Search restored", "The Windows search icon is visible again.");
            }
            else
            {
                SetWindowsSearchMode(0);
                menuItem.Checked = true;
                ShowBalloon("Windows Search hidden",
                    "QuickFind replaces Windows Search.\nUse Alt+Space to search.");
            }
            RestartExplorer();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not change Windows Search setting:\n{ex.Message}",
                "QuickFind", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void SetWindowsSearchMode(int mode)
    {
        using var key = Registry.CurrentUser.CreateSubKey(TaskbarSearchKey);
        key.SetValue(TaskbarSearchValue, mode, RegistryValueKind.DWord);
    }

    private static void RestartExplorer()
    {
        var result = MessageBox.Show(
            "Explorer needs to restart for the change to take effect.\n\n" +
            "Restart now? (Your taskbar will briefly disappear and come back)",
            "QuickFind", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        foreach (var proc in Process.GetProcessesByName("explorer"))
        {
            try { proc.Kill(); } catch { }
        }

        Task.Delay(1500).ContinueWith(_ =>
        {
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start("explorer.exe");
        });
    }

    // ── Indexing ────────────────────────────────────────────────────────

    private async Task IndexAllDrivesAsync()
    {
        if (IndexPersistence.TryLoad(_index) && _index.Count > 1000)
        {
            _searchWindow?.UpdateStatus();
            ShowBalloon("Index loaded", $"{_index.Count:N0} files ready");
            return;
        }
        // Index too small or missing — re-index
        await ReindexAsync();
    }

    private async Task ReindexAsync()
    {
        _indexCts.Cancel();
        _indexCts.Dispose();
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;

        _index.Clear();

        bool elevated = IsElevated();
        var drives = DriveDetector.GetFixedNtfsDrives();
        string driveList = string.Join(", ", drives.Select(d => d.RootDirectory.FullName));
        string mode = elevated ? "MFT (Admin)" : "Standard";
        _searchWindow?.UpdateStatus($"Indexing {driveList} — {mode}...");

        var progress = new Progress<string>(msg =>
            _searchWindow?.UpdateStatus(msg));

        bool anyMft = false;

        await Task.Run(() =>
        {
            foreach (var drive in drives)
            {
                if (ct.IsCancellationRequested) return;
                string root = drive.RootDirectory.FullName;
                bool usedMft = false;

                if (elevated)
                    usedMft = MftIndexer.TryIndex(_index, root, progress, ct);
                if (usedMft) anyMft = true;
                if (!usedMft)
                    FallbackIndexer.Index(_index, root, progress, ct);
            }
        }, ct);

        if (!ct.IsCancellationRequested)
        {
            IndexPersistence.Save(_index);
            _searchWindow?.UpdateStatus();
            string modeStr = anyMft ? "MFT" : "standard";
            ShowBalloon("Indexing complete",
                $"{_index.Count:N0} files indexed ({modeStr} mode)");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void ShowBalloon(string title, string text)
    {
        _notifyIcon?.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info);
    }

    private static void RemoveRegistryAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("QuickFind", false);
        }
        catch { }
    }

    private void ExitApp()
    {
        _indexCts.Cancel();
        IndexPersistence.Save(_index);

        _notifyIcon?.Dispose();
        _notifyIcon = null;

        try { HotkeyManager.Current.Remove("ShowSearch"); } catch { }

        _searchWindow?.ForceClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
