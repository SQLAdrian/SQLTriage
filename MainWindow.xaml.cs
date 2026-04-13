/* In the name of God, the Merciful, the Compassionate */

using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SqlHealthAssessment.Data;
using SqlHealthAssessment.Data.Services;
using System.Windows.Threading;

namespace SqlHealthAssessment
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
            Title = $"SQL Health Assessment v{version}";

            // Add keyboard shortcut for DevTools (F12)
            KeyDown += OnKeyDown;
            Loaded += OnWindowLoaded;
            StateChanged += OnStateChanged;

            // Listen for zoom changes from settings UI
            if (_userSettings != null)
                _userSettings.OnZoomChanged += OnZoomChanged;

            InitializeTrayIcon(version);
        }

        // Tray menu items that update dynamically
        private System.Windows.Forms.ToolStripMenuItem? _trayServerModeItem;
        private System.Windows.Forms.ToolStripMenuItem? _trayOpenBrowserItem;
        private System.Windows.Forms.ToolStripMenuItem? _trayServerStatusItem;   // green/red dot + label
        private System.Windows.Forms.ToolStripMenuItem? _trayServerCountItem;    // connected / total servers

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
                    var iconPath = Path.Combine(AppContext.BaseDirectory, "SQLHealthAssessment.ico");
                    icon = File.Exists(iconPath)
                        ? new System.Drawing.Icon(iconPath)
                        : System.Drawing.SystemIcons.Application;
                }

                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = icon,
                    Text = $"SQL Health Assessment v{version}",
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

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Show Window", null, (_, _) => TrayShow());
                menu.Items.Add("Hide to Tray", null, (_, _) => TrayHide());
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(_trayServerStatusItem);
                menu.Items.Add(_trayServerCountItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(_trayServerModeItem);
                menu.Items.Add(_trayOpenBrowserItem);
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
            var connMgr    = App.Services?.GetService<ServerConnectionManager>();
            var version    = App.Services?.GetService<AutoUpdateService>()?.GetCurrentVersion() ?? "";

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
                _trayIcon.Text = $"SQL Health Assessment v{version}";
            }

            // ── Connected servers row ────────────────────────────────────
            if (_trayServerCountItem != null && connMgr != null)
            {
                var all         = connMgr.GetConnections();
                int total       = all.Count;
                int connected   = all.Count(c => c.IsConnected);
                int disconnected = total - connected;

                if (total == 0)
                {
                    _trayServerCountItem.Text  = "No servers configured";
                    _trayServerCountItem.Image = MakeDotIcon(System.Drawing.Color.FromArgb(120, 120, 120));
                }
                else if (disconnected == 0)
                {
                    _trayServerCountItem.Text  = $"Servers: {connected}/{total} connected";
                    _trayServerCountItem.Image = MakeDotIcon(System.Drawing.Color.FromArgb(60, 200, 80));
                }
                else if (connected == 0)
                {
                    _trayServerCountItem.Text  = $"Servers: 0/{total} connected";
                    _trayServerCountItem.Image = MakeDotIcon(System.Drawing.Color.FromArgb(200, 60, 60));
                }
                else
                {
                    // Mixed — amber
                    _trayServerCountItem.Text  = $"Servers: {connected}/{total} connected  ({disconnected} down)";
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
                    _trayIcon?.ShowBalloonTip(2000, "SQL Health Assessment", "Server mode stopped", System.Windows.Forms.ToolTipIcon.Info);
                }
                else
                {
                    await serverMode.StartAsync();
                    _logger?.LogInformation("Server mode started from tray");
                    Dispatcher.Invoke(UpdateTrayServerStatus);
                    _trayIcon?.ShowBalloonTip(2000, "SQL Health Assessment", $"Server mode started — {serverMode.Url}", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to toggle server mode from tray");
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
                _logger?.LogInformation("Server mode started automatically");
                Dispatcher.Invoke(UpdateTrayServerStatus);
                _trayIcon?.ShowBalloonTip(2000, "SQL Health Assessment", "Server mode started automatically", System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start server mode automatically");
                _trayIcon?.ShowBalloonTip(2000, "SQL Health Assessment", "Failed to start server mode", System.Windows.Forms.ToolTipIcon.Error);
            }
        }

        private void AutoStartServerMode()
        {
            _ = AutoStartServerModeAsync();
        }

        private void TrayExit()
        {
            _forceClose = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            // Normal minimize stays in taskbar — tray-hide is only via tray menu
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // Always attempt WebView2 first, regardless of pre-check result
            try
            {
                BlazorWebView = new Microsoft.AspNetCore.Components.WebView.Wpf.BlazorWebView
                {
                    HostPage = "wwwroot/index.html",
                    Services = App.Services!
                };
                BlazorWebView.RootComponents.Add(
                    new Microsoft.AspNetCore.Components.WebView.Wpf.RootComponent
                    {
                        Selector = "#app",
                        ComponentType = typeof(Components.Layout.MainLayout)
                    });
                BlazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
                BlazorWebView.BlazorWebViewInitializing += OnBlazorWebViewInitializing;
                WebViewHost.Content = BlazorWebView;
                _logger?.LogInformation("MainWindow loaded — BlazorWebView created successfully");
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
                BlazorWebView = null;
                WebViewHost.Content = null;
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

        private void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
        {
            try
            {
                _logger?.LogInformation("BlazorWebView initialized");
                _webView2Initialized = true;
                _coreWebView2 = e.WebView.CoreWebView2;

                // Wire up PrintService with the live CoreWebView2 instance
                var printService = App.Services?.GetService<Data.Services.PrintService>();
                printService?.SetWebView(_coreWebView2);

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

                if (serverMode.Url != null)
                {
                    Process.Start(new ProcessStartInfo(serverMode.Url) { UseShellExecute = true });
                }

                WebView2ErrorTitle.Text = "Server Mode Active";
                WebView2ErrorTitle.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4488ff"));
                WebView2ErrorMessage.Text = $"Access the application in your browser:\n\n" +
                    $"  {serverMode.Url}\n\n" +
                    "Share this URL with other users on your network.\n" +
                    "Keep this window open to maintain the server.";

                ServerModeButton.Content = $"Running at {serverMode.Url}";
                UpdateTrayServerStatus();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start server mode");
                MessageBox.Show($"Failed to start server mode:\n{ex.Message}",
                    "Server Mode Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ServerModeButton.IsEnabled = true;
                ServerModeButton.Content = "Start Server Mode (no WebView2 needed)";
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
            _trayIcon?.Dispose();
            _trayIcon = null;

            // Let WPF shut down naturally
            Application.Current.Shutdown();
        }
    }
}
