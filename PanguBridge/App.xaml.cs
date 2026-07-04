using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Hardcodet.Wpf.TaskbarNotification;
using HIDMaestro;
using PanguBridge.Controllers;
using PanguBridge.Mapping;
using PanguBridge.Views;

namespace PanguBridge;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private PanguEngine? _engine;
    private Mutex? _singleInstanceMutex;

    // Registered window messages are unique system-wide for a given string (OS-maintained
    // table), so both this instance and any later-launched second instance resolve to the
    // same ID here with no coordination needed beyond using the same string.
    private static readonly uint ShowMainWindowMessage =
        NativeMethods.RegisterWindowMessage("PanguBridge-ShowMainWindow");

    private static class NativeMethods
    {
        public static readonly IntPtr HwndBroadcast = new(0xFFFF);
        public const int SwHide = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }

    /// <summary>
    /// Calls <see cref="HMContext.InstallDriver"/> directly. The whole app already runs
    /// elevated via app.manifest's requireAdministrator, so no child-process relaunch is
    /// needed here.
    /// </summary>
    public static (bool Success, string? Error) InstallHidMaestroDriver()
    {
        try
        {
            using var ctx = new HMContext();
            ctx.InstallDriver();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Removes the HIDMaestro driver packages from the Windows driver store.
    /// HMContext exposes no uninstall API (confirmed against the SDK source), so this
    /// replicates the safe removal recipe HIDMaestro's own docs describe: evict every
    /// virtual devnode first via <see cref="HMContext.RemoveAllVirtualControllers"/>, then
    /// a plain (no /uninstall, no /force) pnputil /delete-driver - forcing removal while a
    /// device is still bound leaves it stuck in a "restart required" state.
    /// </summary>
    public static (bool Success, string? Error) UninstallHidMaestroDriver()
    {
        try
        {
            HMContext.RemoveAllVirtualControllers();

            var (enumExit, enumOutput) = RunPnputil("/enum-drivers");
            if (enumExit != 0)
                return (false, $"pnputil /enum-drivers failed (exit {enumExit}): {enumOutput}");

            List<string> publishedNames = FindHidMaestroPublishedNames(enumOutput);
            if (publishedNames.Count == 0)
                return (true, null); // Already gone - idempotent, matching HIDMaestro's own install/uninstall behavior.

            foreach (string published in publishedNames)
            {
                var (delExit, delOutput) = RunPnputil($"/delete-driver {published}");
                if (delExit != 0)
                    return (false, $"pnputil /delete-driver {published} failed (exit {delExit}): {delOutput}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // Reads stdout/stderr via async event callbacks rather than sequential blocking ReadToEnd
    // calls - pnputil /enum-drivers can print well past one pipe buffer's worth on a machine
    // with many driver-store entries, and reading stdout to completion before touching stderr
    // deadlocks the moment the child blocks trying to write to a full, undrained stderr pipe.
    private static (int ExitCode, string Output) RunPnputil(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("pnputil.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        var output = new System.Text.StringBuilder();
        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.OutputDataReceived += (_, args) => { if (args.Data != null) output.AppendLine(args.Data); };
        proc.ErrorDataReceived  += (_, args) => { if (args.Data != null) output.AppendLine(args.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        return (proc.ExitCode, output.ToString());
    }

    // Parses "pnputil /enum-drivers" output blocks, matching entries whose Original Name is
    // hidmaestro.inf or hidmaestro_xusb.inf (HIDMaestro's fixed INF filenames - see the
    // "Driver uninstall" section of HIDMaestro's wiki), returning each match's Published
    // Name (the oemNN.inf identifier /delete-driver requires).
    private static List<string> FindHidMaestroPublishedNames(string enumOutput)
    {
        var results = new List<string>();
        string[] blocks = enumOutput.Replace("\r\n", "\n").Split("\n\n");
        foreach (string block in blocks)
        {
            string[] lines = block.Split('\n');
            bool isHidMaestro = lines.Any(line =>
            {
                int colon = line.IndexOf(':');
                if (colon < 0) return false;
                string label = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                return label.Contains("Original Name", StringComparison.OrdinalIgnoreCase)
                    && (value.Equals("hidmaestro.inf", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("hidmaestro_xusb.inf", StringComparison.OrdinalIgnoreCase));
            });
            if (!isHidMaestro) continue;

            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string label = line[..colon].Trim();
                if (label.Contains("Published Name", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(line[(colon + 1)..].Trim());
                    break;
                }
            }
        }
        return results;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global\ prefix (not just a plain name) so the check works even across different
        // user sessions - the app always runs elevated, so creating a global mutex needs no
        // extra permission here. If another instance already holds it, tell that instance to
        // show its window via a broadcast window message and exit immediately - no engine,
        // no tray icon, no window ever gets created for this second process.
        _singleInstanceMutex = new Mutex(true, "Global\\PanguBridge-SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            NativeMethods.PostMessage(NativeMethods.HwndBroadcast, ShowMainWindowMessage, IntPtr.Zero, IntPtr.Zero);
            Environment.Exit(0);
            return;
        }

        // Logs and reports any crash instead of the process just silently vanishing right
        // after the UAC prompt with no way to tell why - both handlers can fire for genuinely
        // unrecoverable errors, so this only improves diagnosability, it doesn't try to keep
        // the app running through a real crash.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            ReportCrash(args.Exception);
            args.Handled = true;
        };

        ConfigPaths.EnsureDirectoriesExist();

        var settings = AppSettings.LoadOrCreateDefault();
        ThemeManager.Apply(settings.Theme);
        if (!string.IsNullOrWhiteSpace(settings.VJoyInstallDir))
            VJoyNative.TrySetExplicitDirectory(settings.VJoyInstallDir);

        // ButtonMap is the SAME Dictionary instance as settings.VJoyButtonMap (not a copy) -
        // MappingEditor mutates it in place via Profile.ButtonMap, so saving settings after an
        // edit persists it with no separate sync step. See MappingEditor.SaveButton_OnClick.
        var profile = new MappingProfile { Name = "default", ButtonMap = settings.VJoyButtonMap };
        _engine = new PanguEngine(profile, settings);
        _engine.Start();

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.Icon = TryLoadTrayIcon() ?? System.Drawing.SystemIcons.Application;

        _mainWindow = new MainWindow(_engine, settings);
        _mainWindow.Icon = TryLoadWindowIcon();

        // Task Scheduler autostart (see Autostart.cs) passes --minimized so a logon launch
        // stays tray-only instead of popping the window up over whatever the user is doing.
        // EnsureHandle() creates the window's real HWND (needed for the WndProc hook below,
        // and for DarkTitleBar.Apply via MainWindow's SourceInitialized handler) without ever
        // making it visible - Show() is what the manual-launch path uses for that instead.
        bool startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (startMinimized)
            new WindowInteropHelper(_mainWindow).EnsureHandle();
        else
            _mainWindow.Show();

        if (PresentationSource.FromVisual(_mainWindow) is HwndSource hwndSource)
            hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)ShowMainWindowMessage)
        {
            ShowMainWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // Icon.ico is embedded as a WPF Resource (see PanguBridge.csproj) rather than copied loose
    // to the output directory, so it's available via pack URI regardless of the process's
    // working directory. Never let a failure here take down the whole app - it's cosmetic;
    // both callers fall back to a built-in icon instead.
    private static System.Drawing.Icon? TryLoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Icon.ico", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            return info is null ? null : new System.Drawing.Icon(info.Stream);
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.ImageSource? TryLoadWindowIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Icon.ico", UriKind.Absolute);
            return System.Windows.Media.Imaging.BitmapFrame.Create(uri);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Logs the exception, shows it to the user, and exits deliberately rather than
    /// leaving a broken process running with no window and no way to close it (e.g. if
    /// startup fails before the tray icon or main window ever gets created).</summary>
    private static void ReportCrash(Exception? ex)
    {
        string text = ex?.ToString() ?? "Unknown error (no exception object).";
        try
        {
            string logPath = Path.Combine(ConfigPaths.BaseDir, "crash.log");
            File.WriteAllText(logPath, $"{DateTime.Now:O}\n{text}\n");
        }
        catch
        {
            // Best-effort - showing the error to the user still works even if the log write fails.
        }

        MessageBox.Show($"Pangu Bridge hit an unexpected error and needs to close:\n\n{text}",
            "Pangu Bridge - Error", MessageBoxButton.OK, MessageBoxImage.Error);
        Environment.Exit(1);
    }

    private void TrayIcon_OnDoubleClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void TrayIcon_OnOpenClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void TrayIcon_OnExitClick(object sender, RoutedEventArgs e)
    {
        // Close the context menu popup immediately so it doesn't sit there looking frozen
        // while Shutdown()'s engine teardown (thread joins, device release) runs. Setting
        // ContextMenu.IsOpen = false alone was not enough (confirmed - the popup still lingered
        // for the whole teardown window even with that plus a forced dispatcher flush).
        // Hardcodet.NotifyIcon.Wpf's own TaskbarIcon.ShowContextMenu obtains the popup's real
        // HWND via PresentationSource.FromVisual(ContextMenu) to call SetForegroundWindow on it
        // when opening - using that exact same handle here to force it hidden directly via
        // Win32 is more reliable than trusting WPF's own Popup close/teardown sequence to
        // finish before the blocking work below starves it.
        //
        // The tray icon itself is deliberately left alone here - it stays visible for the
        // whole teardown window and only disappears via OnExit()'s _trayIcon.Dispose() once
        // shutdown actually completes, so its disappearance is the user's signal that the app
        // is genuinely closed rather than just unresponsive.
        if (_trayIcon?.ContextMenu is { } menu)
        {
            if (PresentationSource.FromVisual(menu) is HwndSource popupSource)
                NativeMethods.ShowWindow(popupSource.Handle, NativeMethods.SwHide);
            menu.IsOpen = false;
        }

        Shutdown();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Stop();
        _engine?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
