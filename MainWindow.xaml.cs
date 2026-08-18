/* In the name of God, the Merciful, the Compassionate */

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Services;


#pragma warning disable CA1416 // Windows-only API — project targets net8.0-windows
namespace SQLTriage
{
    public partial class MainWindow : Window
    {
        private readonly ILogger<MainWindow>? _logger;
        private readonly UserSettingsService? _userSettings;
        private bool _webView2Initialized = false;
        private bool _suppressZoomSync = false;
        private Microsoft.Web.WebView2.Core.CoreWebView2? _coreWebView2;
        private Microsoft.AspNetCore.Components.WebView.Wpf.BlazorWebView? BlazorWebView;

        // System tray icon
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private bool _forceClose = false;

        public MainWindow()
        {
            InitializeComponent();
            _logger = App.Services?.GetService<ILogger<MainWindow>>();
            _userSettings = App.Services?.GetService<UserSettingsService>();

            var version = App.Services?.GetService<AutoUpdateService>()?.GetCurrentVersion() ?? "0.79.0";
            // Append the build-mode tag after the version so a stale/community binary is
            // obvious at a glance (· Community / · Full / · DEVBRIDGE). See BuildMode.
            Title = $"SQLTriage v{version}{SQLTriage.Data.BuildMode.TitleSuffix}";

            // Add keyboard shortcut for DevTools (F12)
            KeyDown += OnKeyDown;
            Loaded += OnWindowLoaded;
            StateChanged += OnStateChanged;

            // Listen for zoom changes from settings UI
            if (_userSettings != null)
            {
                _userSettings.OnZoomChanged += OnZoomChanged;
            }

            InitializeTrayIcon(version);
        }

        // Tray menu items that update dynamically
        private System.Windows.Forms.ToolStripMenuItem? _trayServerModeItem;
        private System.Windows.Forms.ToolStripMenuItem? _trayOpenBrowserItem;
        private System.Windows.Forms.ToolStripMenuItem? _trayServerStatusItem;   // green/red dot + label
        private System.Windows.Forms.ToolStripMenuItem? _trayServerCountItem;    // connected / total servers
        private System.Windows.Forms.ToolStripMenuItem? _trayMcpServerItem;      // MCP server toggle
        private System.Diagnostics.Process? _mcpProcess;                        // MCP server process

        /// <summary>
        /// Renders a 12×12 filled circle bitmap in the given colour — used as status dot icons in the tray menu.
        /// </summary>
        private static System.Drawing.Bitmap MakeDotIcon(System.Drawing.Color colour)
        {
            var bmp = new System.Drawing.Bitmap(12, 12, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(colour);
            g.FillEllipse(brush, 1, 1, 10, 10);
            return bmp;
        }

        private void InitializeTrayIcon(string version)
        {
            try
            {
                // Extract icon from the running exe so tray matches the taskbar icon
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                System.Drawing.Icon? icon = null;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon == null)
                {
                    var iconPath = Path.Combine(AppContext.BaseDirectory, "SQLTriage.ico");
                    icon = File.Exists(iconPath)
                        ? new System.Drawing.Icon(iconPath)
                        : System.Drawing.SystemIcons.Application;
                }

                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = icon,
                    Text = $"SQLTriage v{version}",
                    Visible = true
                };

                // Status indicator row — dot icon + text, not clickable
                _trayServerStatusItem = new System.Windows.Forms.ToolStripMenuItem("● Server mode: off")
                {
                    Enabled = false,
                    Image = MakeDotIcon(System.Drawing.Color.FromArgb(180, 180, 180))
                };

                // Connected servers summary row, not clickable
                _trayServerCountItem = new System.Windows.Forms.ToolStripMenuItem("No servers configured")
                {
                    Enabled = false,
                    Image = MakeDotIcon(System.Drawing.Color.FromArgb(120, 120, 120))
                };

                _trayOpenBrowserItem = new System.Windows.Forms.ToolStripMenuItem("Open in Browser", null, (_, _) => TrayOpenBrowser());
                _trayOpenBrowserItem.Enabled = false;

                _trayServerModeItem = new System.Windows.Forms.ToolStripMenuItem("Start Server Mode", null, (_, _) => TrayToggleServerMode());

                _trayMcpServerItem = new System.Windows.Forms.ToolStripMenuItem("Start MCP Server", null, (_, _) => TrayToggleMcpServer());

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Show Window", null, (_, _) => TrayShow());
                menu.Items.Add("Hide to Tray", null, (_, _) => TrayHide());
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(_trayServerStatusItem);
                menu.Items.Add(_trayServerCountItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(_trayServerModeItem);
                menu.Items.Add(_trayMcpServerItem);
                menu.Items.Add(_trayOpenBrowserItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

                // Feedback submenu
                var feedbackMenu = new System.Windows.Forms.ToolStripMenuItem("Report / Feedback");
                feedbackMenu.Image = MakeDotIcon(System.Drawing.Color.FromArgb(99, 102, 241));
                feedbackMenu.DropDownItems.Add("Report a Bug (GitHub)", null, (_, _) => OpenUrl("https://github.com/SQLAdrian/SQLTriage/issues/new"));
                feedbackMenu.DropDownItems.Add("Feature Request (GitHub Discussions)", null, (_, _) => OpenUrl("https://github.com/SQLAdrian/SQLTriage/discussions/new?category=ideas"));
                feedbackMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
                feedbackMenu.DropDownItems.Add("Email: adrian@sqldba.org", null, (_, _) => OpenUrl("mailto:adrian@sqldba.org?subject=SQLTriage Feedback"));
                feedbackMenu.DropDownItems.Add("LinkedIn: milliondollardba", null, (_, _) => OpenUrl("https://www.linkedin.com/in/milliondollardba/"));
                feedbackMenu.DropDownItems.Add("sqldba.org", null, (_, _) => OpenUrl("https://sqldba.org"));
                menu.Items.Add(feedbackMenu);

                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Exit", null, (_, _) => TrayExit());
                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += (_, _) => TrayShow();

                // Refresh status counts each time the menu opens
                menu.Opening += (_, _) => UpdateTrayServerStatus();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create tray icon");
            }
        }

        private void UpdateTrayServerStatus()
        {
            if (_trayIcon == null || _trayServerModeItem == null || _trayOpenBrowserItem == null) return;

            var serverMode = App.Services?.GetService<ServerModeService>();
            var connMgr = App.Services?.GetService<ServerConnectionManager>();
            var version = App.Services?.GetService<AutoUpdateService>()?.GetCurrentVersion() ?? "";

            // ── Server mode row ──────────────────────────────────────────
            bool running = serverMode?.IsRunning == true;
            if (_trayServerStatusItem != null)
            {
                _trayServerStatusItem.Image = MakeDotIcon(running
                    ? System.Drawing.Color.FromArgb(60, 200, 80)    // green
                    : System.Drawing.Color.FromArgb(200, 60, 60));   // red
                _trayServerStatusItem.Text = running
                    ? $"Server mode: running  ({serverMode!.Url})"
                    : "Server mode: off";
            }

            if (running)
            {
                _trayServerModeItem.Text = $"Stop Server Mode";
                _trayOpenBrowserItem.Text = $"Open {serverMode!.Url}";
                _trayOpenBrowserItem.Enabled = true;
                // Tooltip max 63 chars
                var tip = $"SHA v{version} — server {serverMode.Url}";
                _trayIcon.Text = tip.Length > 63 ? tip[..63] : tip;
            }
            else
            {
                _trayServerModeItem.Text = "Start Server Mode";
                _trayOpenBrowserItem.Text = "Open in Browser";
                _trayOpenBrowserItem.Enabled = false;
                _trayIcon.Text = $"SQLTriage v{version}";
            }

            // ── Connected servers row ────────────────────────────────────
            if (_trayServerCountItem != null && connMgr != null)
            {
                var all = connMgr.GetConnections();
                int total = all.Count;
                int connected = all.Count(c => c.IsConnected);
                int disconnected = total - connected;

                if (total == 0)
                {
                    _trayServerCountItem.Text = "No servers configured";
                    _trayServerCountItem.Image = MakeDotIcon(System.Drawing.Color.FromArgb(120, 120, 120));
                }
                else if (disconnected == 0)
                {
                    _trayServerCountItem.Text = $"Servers: {connected}/{total} connected";
                    _trayServerCountItem.Image = MakeDotIcon(System.Drawing.Color.FromArgb(60, 200, 80));
                }
                else if (connected == 0)
                {
                    _trayServerCountItem.Text = $"Servers: 0/{total} connected";
                    _trayServerCountItem.Image = MakeDotIcon(System.Drawing.Color.FromArgb(200, 60, 60));
                }
                else
                {
                    // Mixed — amber
                    _trayServerCountItem.Text = $"Servers: {connected}/{total} connected  ({disconnected} down)";
                    _trayServerCountItem.Image = MakeDotIcon(System.Drawing.Color.FromArgb(220, 160, 30));
                }
            }
        }

        private void TrayShow()
        {
            Show();
            WindowState = WindowState.Maximized;
            Activate();
        }

        private void TrayHide()
        {
            Hide();
        }

        private void TrayOpenBrowser()
        {
            var serverMode = App.Services?.GetService<ServerModeService>();
            if (serverMode?.IsRunning == true && serverMode.Url != null)
            {
                Process.Start(new ProcessStartInfo(serverMode.Url) { UseShellExecute = true });
            }
        }

        private async Task TrayToggleServerModeAsync()
        {
            try
            {
                var serverMode = App.Services?.GetService<ServerModeService>();
                if (serverMode == null) return;

                if (serverMode.IsRunning)
                {
                    await serverMode.StopAsync();
                    _logger?.LogInformation("Server mode stopped from tray");
                    Dispatcher.Invoke(UpdateTrayServerStatus);
                    _trayIcon?.ShowBalloonTip(2000, "SQLTriage", "Server mode stopped", System.Windows.Forms.ToolTipIcon.Info);
                }
                else
                {
                    await serverMode.StartAsync();
                    _logger?.LogInformation("Server mode started from tray");
                    Dispatcher.Invoke(UpdateTrayServerStatus);
                    _trayIcon?.ShowBalloonTip(2000, "SQLTriage", $"Server mode started — {serverMode.Url}", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to toggle server mode from tray");
            }
        }

        private void TrayToggleMcpServer()
        {
            try
            {
                if (_mcpProcess != null && !_mcpProcess.HasExited)
                {
                    // Stop MCP server
                    _mcpProcess.Kill();
                    _mcpProcess.Dispose();
                    _mcpProcess = null;
                    _trayMcpServerItem!.Text = "Start MCP Server";
                    _trayIcon?.ShowBalloonTip(2000, "SQLTriage", "MCP server stopped", System.Windows.Forms.ToolTipIcon.Info);
                    _logger?.LogInformation("MCP server stopped from tray");
                }
                else
                {
                    // Start MCP server
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = "mcp_server.py",
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    _mcpProcess = System.Diagnostics.Process.Start(psi);
                    _trayMcpServerItem!.Text = "Stop MCP Server";
                    _trayIcon?.ShowBalloonTip(2000, "SQLTriage", "MCP server started", System.Windows.Forms.ToolTipIcon.Info);
                    _logger?.LogInformation("MCP server started from tray");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to toggle MCP server from tray");
                _trayIcon?.ShowBalloonTip(2000, "SQLTriage", "Failed to toggle MCP server", System.Windows.Forms.ToolTipIcon.Error);
            }
        }

        private void TrayToggleServerMode()
        {
            _ = TrayToggleServerModeAsync();
        }

        private async Task AutoStartServerModeAsync()
        {
            if (App.Services == null) return;
            var serverMode = App.Services.GetService<ServerModeService>();
            if (serverMode == null) return;

            try
            {
                await serverMode.StartAsync();
                var url = serverMode.Url;
                _logger?.LogInformation("Server mode started automatically at {Url}", url);
                Dispatcher.Invoke(() => ShowServerRunningState(url));
                if (url != null) OpenUrl(url);
                _trayIcon?.ShowBalloonTip(2000, "SQLTriage", $"Server mode started — {url}", System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start server mode automatically");
                Dispatcher.Invoke(() => ShowServerErrorState(
                    "SQLTriage could not start its built-in web server.\n\n" + ex.Message +
                    "\n\nClick “Retry Server Mode” below to try again."));
                _trayIcon?.ShowBalloonTip(2000, "SQLTriage", "Failed to start server mode", System.Windows.Forms.ToolTipIcon.Error);
            }
        }

        private void AutoStartServerMode()
        {
            _ = AutoStartServerModeAsync();
        }

        /// <summary>
        /// Explicitly disposes the BlazorWebView and its underlying WebView2 control
        /// to prevent zombie msedgewebview2.exe processes that lock the user-data folder
        /// and cause relaunch hangs.
        /// </summary>
        private void DisposeWebView()
        {
            if (BlazorWebView == null) return;

            try
            {
                BlazorWebView.BlazorWebViewInitialized -= OnBlazorWebViewInitialized;
                BlazorWebView.BlazorWebViewInitializing -= OnBlazorWebViewInitializing;

                var wv = BlazorWebView.WebView;
                if (wv?.CoreWebView2 != null)
                {
                    try { wv.Dispose(); } catch { /* best effort */ }
                }

                // Let WPF shut down naturally - WebView2 will be cleaned up by the OS
                // when the process exits. Force-killing causes issues with other apps.
                // This is acceptable because we're closing the entire application.
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error during WebView disposal");
            }
            finally
            {
                WebViewHost.Content = null;
                BlazorWebView = null;
            }
        }

        private void TrayExit()
        {
            _forceClose = true;
            DisposeWebView();
            if (_mcpProcess != null && !_mcpProcess.HasExited)
            {
                _mcpProcess.Kill();
                _mcpProcess.Dispose();
            }
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* best-effort */ }
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            // Normal minimize stays in taskbar — tray-hide is only via tray menu
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            _logger?.LogDebug("[MAINWINDOW] OnWindowLoaded started");

            // If the startup pre-check already determined WebView2 is unavailable, don't even
            // construct the BlazorWebView — it would only throw asynchronously and surface as an
            // unobserved task exception. Go straight to server mode for a clean, fast fallback.
            if (!App.WebView2Available)
            {
                _logger?.LogWarning("WebView2 unavailable per startup pre-check — switching directly to server mode");
                SwitchToServerMode();
                return;
            }

            // Otherwise attempt WebView2; if init still fails we fall back via the dispatcher handler.
            try
            {
                _logger?.LogDebug("[MAINWINDOW] Creating BlazorWebView...");
                var sw = System.Diagnostics.Stopwatch.StartNew();

                BlazorWebView = new Microsoft.AspNetCore.Components.WebView.Wpf.BlazorWebView
                {
                    HostPage = "wwwroot/index.html",
                    Services = App.Services!
                };
                _logger?.LogDebug("[MAINWINDOW] BlazorWebView instance created, adding root components...");

                BlazorWebView.RootComponents.Add(
                    new Microsoft.AspNetCore.Components.WebView.Wpf.RootComponent
                    {
                        Selector = "#app",
                        ComponentType = typeof(Components.Layout.MainLayout)
                    });
                _logger?.LogDebug("[MAINWINDOW] Root components added, wiring events...");

                BlazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
                BlazorWebView.BlazorWebViewInitializing += OnBlazorWebViewInitializing;
                WebViewHost.Content = BlazorWebView;

                sw.Stop();
                _logger?.LogInformation("MainWindow loaded — BlazorWebView created successfully in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "BlazorWebView failed — falling back to server mode");
                BlazorWebView = null;
                SwitchToServerMode();
            }
        }

        private void OnBlazorWebViewInitializing(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializingEventArgs e)
        {
            // This fires before WebView2 creation — if WebView2 is missing,
            // the exception occurs AFTER this point during async initialization
            // and surfaces as TargetInvocationException through the dispatcher.
            // We hook into Dispatcher.UnhandledException in App.xaml.cs to catch it.
        }

        /// <summary>
        /// Called from App.xaml.cs when a WebView2 exception surfaces through the dispatcher.
        /// </summary>
        public void FallbackToServerMode()
        {
            Dispatcher.Invoke(() =>
            {
                _logger?.LogWarning("FallbackToServerMode triggered from dispatcher exception");
                DisposeWebView();
                SwitchToServerMode();
            });
        }

        /// <summary>
        /// Shrinks the window to a compact size for server mode and auto-starts Blazor Server.
        /// </summary>
        private void SwitchToServerMode()
        {
            WindowState = WindowState.Normal;
            Width = 700;
            Height = 450;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // Re-center after resize
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;

            // Show the overlay so the user sees something while the server starts
            WebView2ErrorTitle.Text = "Starting Server Mode…";
            WebView2ErrorTitle.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4488ff"));
            WebView2ErrorMessage.Text = "WebView2 is not available on this machine.\n\n" +
                "Starting the built-in web server — you will be able to access the\n" +
                "application in any browser once it is ready.\n\n" +
                "Click 'Start Server Mode' below if it does not start automatically.";
            WebView2ErrorOverlay.Visibility = Visibility.Visible;

            AutoStartServerMode();
        }

        /// <summary>Switch the overlay to the "server is running" state with the browsable URL.</summary>
        private void ShowServerRunningState(string? url)
        {
            WebView2ErrorTitle.Text = "Server Mode Active";
            WebView2ErrorTitle.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4488ff"));
            WebView2ErrorMessage.Text =
                "WebView2 isn't available on this machine, so SQLTriage is now running as a local web server. " +
                "Open the address below in any browser on this machine or your network.";
            WebView2InstallPanel.Visibility = Visibility.Collapsed;
            ServerModeInfoPanel.Visibility = Visibility.Visible;
            ServerModeUrlText.Text = url ?? "(starting…)";
            WebView2ErrorOverlay.Visibility = Visibility.Visible;
            UpdateTrayServerStatus();
        }

        /// <summary>Switch the overlay to a visible, retryable error state instead of a blocking popup.</summary>
        private void ShowServerErrorState(string message)
        {
            WebView2ErrorTitle.Text = "Server Mode Failed to Start";
            WebView2ErrorTitle.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ff4444"));
            WebView2ErrorMessage.Text = message;
            ServerModeInfoPanel.Visibility = Visibility.Collapsed;
            WebView2InstallPanel.Visibility = Visibility.Visible;
            ServerModeButton.IsEnabled = true;
            ServerModeButton.Content = "Retry Server Mode";
            WebView2ErrorOverlay.Visibility = Visibility.Visible;
        }

        private void OnOpenServerUrlClick(object sender, RoutedEventArgs e)
        {
            var url = ServerModeUrlText?.Text;
            if (!string.IsNullOrWhiteSpace(url) && url != "(starting…)") OpenUrl(url);
        }

        private void OnCopyServerUrlClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = ServerModeUrlText?.Text;
                if (!string.IsNullOrWhiteSpace(url)) System.Windows.Clipboard.SetText(url);
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to copy server URL"); }
        }

        private void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
        {
            try
            {
                _logger?.LogInformation("BlazorWebView initialized");
                _webView2Initialized = true;
                _coreWebView2 = e.WebView.CoreWebView2;

                // Wire up parallax background: push monitor+window geometry into
                // the WebView whenever the window moves, resizes, or changes DPI.
                LocationChanged += OnParallaxGeometryChanged;
                SizeChanged += OnParallaxGeometryChanged;
                DpiChanged += OnParallaxGeometryChanged;
                // Push initial state once navigation completes (host page must be
                // loaded before postMessage can reach the JS listener).
                e.WebView.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess) PushParallaxGeometry();
                };

                // Wire up PrintService with the live CoreWebView2 instance
                var printService = App.Services?.GetService<Data.Services.PrintService>();
                printService?.SetWebView(_coreWebView2);

                // Hand the live WebView2 to DevBridge (only present when --devbridge was set).
                // GetService returns null if the service wasn't registered, which is the
                // off-by-default state — no behaviour change for normal users.
#if DEBUG && !SQLT_NO_DEVTOOLS
                App.Services?.GetService<Data.Services.DevBridgeService>()?.SetWebView(_coreWebView2);
#endif

                // Enable Ctrl+scroll zoom and sync back to settings
                _coreWebView2.Settings.IsZoomControlEnabled = true;
                _coreWebView2.Settings.IsPinchZoomEnabled = true;
                e.WebView.ZoomFactorChanged += OnWebViewZoomFactorChanged;

                // Apply saved zoom level after navigation completes (so it sticks)
                e.WebView.NavigationCompleted += (s, args) =>
                {
                    if (args.IsSuccess)
                    {
                        _logger?.LogDebug("WebView navigation completed successfully");
                        ApplyZoom(_userSettings?.GetZoomLevel() ?? 150);
                    }
                    else
                    {
                        _logger?.LogError("WebView navigation failed");
                    }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during BlazorWebView initialization");
                BlazorWebView = null;
                SwitchToServerMode();
            }
        }

        // ── Parallax background plumbing ────────────────────────────────────
        // Anchors the ambient background to the monitor (not the window) by
        // pushing geometry into the WebView. Two layers of throttling:
        //   1) WPF events (LocationChanged etc.) just flip a dirty flag —
        //      they fire at mouse-move rate (often >120 Hz on modern mice)
        //      and a JSON-serialize + cross-process PostWebMessageAsString
        //      per tick was the source of drag jank.
        //   2) A DispatcherTimer at ~60 Hz drains the flag, so we IPC at most
        //      once per frame regardless of input rate.
        // The browser side has its own rAF coalescing on top of this.
        private DispatcherTimer? _parallaxTimer;
        private bool _parallaxDirty;
        private int _lastMonitorW, _lastMonitorH, _lastWindowX, _lastWindowY;

        private void OnParallaxGeometryChanged(object? sender, EventArgs e)
        {
            _parallaxDirty = true;
            // Lazy-start the timer on first event so we don't pay the tick
            // cost during sessions where the window never moves.
            if (_parallaxTimer == null)
            {
                _parallaxTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _parallaxTimer.Tick += (_, _) =>
                {
                    if (_parallaxDirty) PushParallaxGeometry();
                };
                _parallaxTimer.Start();
            }
        }

        private void PushParallaxGeometry()
        {
            _parallaxDirty = false;
            if (_coreWebView2 == null) return;

            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(hMonitor, ref mi)) return;

                // Convert monitor rect (device pixels) → CSS pixels using this
                // window's current DPI scale. WPF's CompositionTarget gives us
                // a transform that already accounts for per-monitor DPI.
                var src = PresentationSource.FromVisual(this);
                double dpiX = 1.0, dpiY = 1.0;
                if (src?.CompositionTarget != null)
                {
                    var m = src.CompositionTarget.TransformToDevice;
                    dpiX = m.M11;
                    dpiY = m.M22;
                }

                int monitorW = (int)((mi.rcMonitor.Right - mi.rcMonitor.Left) / dpiX);
                int monitorH = (int)((mi.rcMonitor.Bottom - mi.rcMonitor.Top) / dpiY);
                // Window.Left/Top are already in CSS-equivalent DIPs at the
                // window's DPI; subtract monitor origin (also DIPs after divide).
                int windowX = (int)(this.Left - (mi.rcMonitor.Left / dpiX));
                int windowY = (int)(this.Top - (mi.rcMonitor.Top / dpiY));

                // Clamp to non-negative so a window slightly off-screen (e.g.
                // mid-drag across monitors) doesn't push the image outside.
                if (windowX < 0) windowX = 0;
                if (windowY < 0) windowY = 0;

                // Skip identical ticks — saves an IPC + JSON parse on the
                // browser side when the OS reports the same coords twice.
                if (monitorW == _lastMonitorW && monitorH == _lastMonitorH &&
                    windowX == _lastWindowX && windowY == _lastWindowY) return;
                _lastMonitorW = monitorW; _lastMonitorH = monitorH;
                _lastWindowX = windowX; _lastWindowY = windowY;

                var json = $"{{\"type\":\"parallax\",\"monitorW\":{monitorW},\"monitorH\":{monitorH},\"windowX\":{windowX},\"windowY\":{windowY}}}";
                _coreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Parallax geometry push failed");
            }
        }

        // P/Invoke for monitor info — needed because WPF's SystemParameters
        // only describe the *primary* screen, not the monitor the window is
        // currently on.
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private void ApplyZoom(int zoomPercent)
        {
            if (BlazorWebView == null) return;
            try
            {
                _suppressZoomSync = true;
                BlazorWebView.WebView.ZoomFactor = zoomPercent / 100.0;
                _suppressZoomSync = false;
                _logger?.LogInformation("Zoom set to {ZoomPercent}%", zoomPercent);
            }
            catch (Exception ex)
            {
                _suppressZoomSync = false;
                _logger?.LogWarning(ex, "Failed to set zoom to {ZoomPercent}%", zoomPercent);
            }
        }

        private void OnZoomChanged(int zoomPercent)
        {
            Dispatcher.Invoke(() => ApplyZoom(zoomPercent));
        }

        private void OnWebViewZoomFactorChanged(object? sender, object e)
        {
            if (_suppressZoomSync || _userSettings == null || BlazorWebView == null) return;
            try
            {
                var newPercent = (int)Math.Round(BlazorWebView.WebView.ZoomFactor * 100);
                // Only save if it actually changed (avoids infinite loop with OnZoomChanged)
                if (newPercent != _userSettings.GetZoomLevel())
                {
                    _suppressZoomSync = true;
                    _userSettings.SetZoomLevel(newPercent);
                    _suppressZoomSync = false;
                }
            }
            catch { /* non-critical */ }
        }

        private void ShowWebView2Error(string? message = null)
        {
            Dispatcher.Invoke(() =>
            {
                var errorMsg = message ?? App.WebView2ErrorMessage ?? "WebView2 runtime is not available";
                WebView2ErrorMessage.Text = $"Error: {errorMsg}\n\nWindows Version: {WebView2Helper.GetWindowsVersion()}\n\n" +
                    "The application requires Microsoft Edge WebView2 Runtime to function. " +
                    "Please install the runtime and restart the application.";
                WebView2ErrorOverlay.Visibility = Visibility.Visible;
            });
        }

        private async Task OnInstallWebView2ClickAsync(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Button;
            if (btn != null) { btn.IsEnabled = false; btn.Content = "Installing..."; }
            try
            {
                var helper = new WebView2Helper();
                var result = await helper.TryInstallWebView2Async();
                if (result.Success)
                    MessageBox.Show("WebView2 installed. Please restart the application.", "Installation Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"WebView2 installation failed: {result.ErrorMessage}", "Installation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to install WebView2");
                MessageBox.Show($"An error occurred: {ex.Message}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Install WebView2"; }
            }
        }

        private void OnInstallWebView2Click(object sender, RoutedEventArgs e)
        {
            _ = OnInstallWebView2ClickAsync(sender, e);
        }

        private void OnRetryClick(object sender, RoutedEventArgs e)
        {
            // Check if WebView2 is now available
            var helper = App.WebView2Helper ?? new WebView2Helper(null);
            var status = helper.CheckWebView2StatusAsync().GetAwaiter().GetResult();

            if (status.IsInstalled && status.IsCompatible)
            {
                _logger?.LogInformation("WebView2 is now available. Version: {Version}. Restart required.", status.Version);
                MessageBox.Show($"WebView2 Runtime is now available (v{status.Version}).\n\nPlease restart the application to use the full UI.",
                    "Restart Required", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _logger?.LogWarning("WebView2 still not available");
                MessageBox.Show("WebView2 runtime is still not available.\n\n" +
                    "Please install the runtime and click Retry.",
                    "WebView2 Not Available", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void OnServerModeClick(object sender, RoutedEventArgs e)
        {
            try
            {
                ServerModeButton.IsEnabled = false;
                ServerModeButton.Content = "Starting server...";

                var serverMode = App.Services?.GetService<ServerModeService>();
                if (serverMode == null)
                {
                    MessageBox.Show("Server mode service not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await serverMode.StartAsync();
                _logger?.LogInformation("Server mode started at {Url}", serverMode.Url);

                if (serverMode.Url != null) OpenUrl(serverMode.Url);
                ShowServerRunningState(serverMode.Url);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start server mode");
                ShowServerErrorState("SQLTriage could not start its built-in web server.\n\n" + ex.Message +
                    "\n\nClick “Retry Server Mode” to try again.");
            }
        }

        private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F12 && BlazorWebView != null)
            {
                try
                {
                    BlazorWebView.WebView.CoreWebView2.OpenDevToolsWindow();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to open DevTools");
                }
            }
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_userSettings != null)
                _userSettings.OnZoomChanged -= OnZoomChanged;

            if (_forceClose)
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
                return;
            }

            ClosingOverlay.Visibility = Visibility.Visible;
            e.Cancel = true;

            try
            {
                var serverMode = App.Services?.GetService<ServerModeService>();
                if (serverMode?.IsRunning == true)
                {
                    _logger?.LogInformation("Stopping server mode during window close...");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try
                    {
                        await serverMode.StopAsync().WaitAsync(cts.Token);
                        _logger?.LogInformation("Server mode stopped successfully");
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.LogWarning("Server mode stop timed out after 8 seconds");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error stopping server mode during close");
            }

            _forceClose = true;
            DisposeWebView();
            _trayIcon?.Dispose();
            _trayIcon = null;

            // Let WPF shut down naturally
            Application.Current.Shutdown();
        }
    }
}
#pragma warning restore CA1416
