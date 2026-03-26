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

                _trayOpenBrowserItem = new System.Windows.Forms.ToolStripMenuItem("Open in Browser", null, (_, _) => TrayOpenBrowser());
                _trayOpenBrowserItem.Enabled = false;

                _trayServerModeItem = new System.Windows.Forms.ToolStripMenuItem("Start Server Mode", null, (_, _) => TrayToggleServerMode());

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Show Window", null, (_, _) => TrayShow());
                menu.Items.Add("Hide to Tray", null, (_, _) => TrayHide());
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(_trayServerModeItem);
                menu.Items.Add(_trayOpenBrowserItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Exit", null, (_, _) => TrayExit());
                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += (_, _) => TrayShow();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create tray icon");
            }
        }

        private void UpdateTrayServerStatus()
        {
            var serverMode = App.Services?.GetService<ServerModeService>();
            if (serverMode == null || _trayServerModeItem == null || _trayOpenBrowserItem == null) return;

            if (serverMode.IsRunning)
            {
                _trayServerModeItem.Text = $"Stop Server Mode ({serverMode.Url})";
                _trayOpenBrowserItem.Text = $"Open {serverMode.Url}";
                _trayOpenBrowserItem.Enabled = true;
                _trayIcon!.Text = $"SQL Health Assessment — Server: {serverMode.Url}";
            }
            else
            {
                _trayServerModeItem.Text = "Start Server Mode";
                _trayOpenBrowserItem.Text = "Open in Browser";
                _trayOpenBrowserItem.Enabled = false;
                var version = App.Services?.GetService<AutoUpdateService>()?.GetCurrentVersion() ?? "";
                _trayIcon!.Text = $"SQL Health Assessment v{version}";
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

        private async void TrayToggleServerMode()
        {
            try
            {
                var serverMode = App.Services?.GetService<ServerModeService>();
                if (serverMode == null) return;

                if (serverMode.IsRunning)
                {
                    await serverMode.StopAsync();
                    _logger?.LogInformation("Server mode stopped from tray");
                    _trayIcon?.ShowBalloonTip(2000, "SQL Health Assessment", "Server mode stopped", System.Windows.Forms.ToolTipIcon.Info);
                }
                else
                {
                    serverMode.EnableHttps = false;
                    await serverMode.StartAsync();
                    _logger?.LogInformation("Server mode started from tray at {Url}", serverMode.Url);
                    _trayIcon?.ShowBalloonTip(2000, "SQL Health Assessment", $"Server mode started at {serverMode.Url}", System.Windows.Forms.ToolTipIcon.Info);

                    // Open browser automatically
                    if (serverMode.Url != null)
                        Process.Start(new ProcessStartInfo(serverMode.Url) { UseShellExecute = true });
                }

                UpdateTrayServerStatus();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to toggle server mode from tray");
                _trayIcon?.ShowBalloonTip(3000, "SQL Health Assessment", $"Server mode error: {ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
            }
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
            // Minimize to tray
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                _trayIcon?.ShowBalloonTip(1500, "SQL Health Assessment", "Minimized to system tray", System.Windows.Forms.ToolTipIcon.Info);
            }
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

        private async void AutoStartServerMode()
        {
            // Show the overlay immediately with a "starting" message
            Dispatcher.Invoke(() =>
            {
                WebView2ErrorTitle.Text = "Starting Server Mode...";
                WebView2ErrorTitle.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4488ff"));
                WebView2ErrorMessage.Text = "WebView2 Runtime is not installed.\n\nStarting Blazor Server so you can access the app in your browser...";
                WebView2ErrorOverlay.Visibility = Visibility.Visible;
                // Hide buttons that aren't needed during auto-start
                InstallWebView2Button.Visibility = Visibility.Collapsed;
                RetryButton.Visibility = Visibility.Collapsed;
                ServerModeButton.Visibility = Visibility.Collapsed;
            });

            try
            {
                var serverMode = App.Services?.GetService<ServerModeService>();
                if (serverMode == null)
                {
                    _logger?.LogError("ServerModeService not available");
                    ShowWebView2Error("Server mode service not available. Please install WebView2 Runtime.");
                    return;
                }

                // Disable HTTPS for auto-start — not needed for local/fallback use
                serverMode.EnableHttps = false;
                await serverMode.StartAsync();

                _logger?.LogInformation("Server mode auto-started at {Url}", serverMode.Url);

                // Open in default browser
                if (serverMode.Url != null)
                {
                    Process.Start(new ProcessStartInfo(serverMode.Url) { UseShellExecute = true });
                }

                // Update overlay to show the running URL
                Dispatcher.Invoke(() =>
                {
                    WebView2ErrorTitle.Text = "Server Mode Active";
                    WebView2ErrorMessage.Text = $"WebView2 Runtime is not installed — running in server mode instead.\n\n" +
                        $"Access the application in your browser:\n\n" +
                        $"  {serverMode.Url}\n\n" +
                        "Share this URL with other users on your network.\n" +
                        "Keep this window open to maintain the server.";
                    ServerModeButton.Visibility = Visibility.Visible;
                    ServerModeButton.Content = $"Running at {serverMode.Url}";
                    ServerModeButton.IsEnabled = false;
                    UpdateTrayServerStatus();
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to auto-start server mode");
                // Fall back to showing the manual options
                ShowWebView2Error($"Failed to auto-start server mode: {ex.Message}\n\nPlease install WebView2 Runtime.");
                Dispatcher.Invoke(() =>
                {
                    InstallWebView2Button.Visibility = Visibility.Visible;
                    RetryButton.Visibility = Visibility.Visible;
                    ServerModeButton.Visibility = Visibility.Visible;
                    ServerModeButton.IsEnabled = true;
                    ServerModeButton.Content = "Start Server Mode (no WebView2 needed)";
                });
            }
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

        private async void OnInstallWebView2Click(object sender, RoutedEventArgs e)
        {
            try
            {
                InstallWebView2Button.IsEnabled = false;
                InstallWebView2Button.Content = "Installing...";

                _logger?.LogInformation("Starting WebView2 installation...");
                
                var helper = App.WebView2Helper ?? new WebView2Helper(null);
                var result = await helper.TryInstallWebView2Async();

                if (result.Success)
                {
                    _logger?.LogInformation("WebView2 installed successfully. Version: {Version}", result.Version);
                    MessageBox.Show($"WebView2 Runtime installed successfully!\nVersion: {result.Version}\n\nPlease restart the application.",
                        "Installation Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    Application.Current.Shutdown();
                }
                else
                {
                    _logger?.LogError("WebView2 installation failed: {Error}", result.ErrorMessage);
                    MessageBox.Show($"Failed to install WebView2:\n{result.ErrorMessage}\n\n" +
                        "Please install manually from:\nhttps://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                        "Installation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during WebView2 installation");
                MessageBox.Show($"Error during installation:\n{ex.Message}",
                    "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                InstallWebView2Button.IsEnabled = true;
                InstallWebView2Button.Content = "Auto-Install WebView2";
            }
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

                // Open in default browser
                if (serverMode.Url != null)
                {
                    Process.Start(new ProcessStartInfo(serverMode.Url) { UseShellExecute = true });
                }

                // Update the error overlay to show success
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
            // Unsubscribe zoom event to prevent leak
            if (_userSettings != null)
                _userSettings.OnZoomChanged -= OnZoomChanged;

            if (_forceClose)
            {
                // Tray "Exit" was clicked — shut down for real
                _trayIcon?.Dispose();
                _trayIcon = null;
                return;
            }

            // Show the closing dialog
            ClosingOverlay.Visibility = Visibility.Visible;

            // Cancel the close to allow dialog to show
            e.Cancel = true;

            // Wait 0.8 seconds then close
            await Task.Delay(800);

            // Stop server mode explicitly before shutdown to release Kestrel threads
            try
            {
                var serverMode = App.Services?.GetService<ServerModeService>();
                if (serverMode?.IsRunning == true)
                    await serverMode.StopAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error stopping server mode during close");
            }

            // Actually close the window
            _forceClose = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }
    }
}
