/* In the name of God, the Merciful, the Compassionate */

using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SqlHealthAssessment.Data;
using System.Windows.Threading;

namespace SqlHealthAssessment
{
    public partial class MainWindow : Window
    {
        private readonly ILogger<MainWindow>? _logger;

        public MainWindow()
        {
            InitializeComponent();
            _logger = App.Services?.GetService<ILogger<MainWindow>>();

            var version = App.Services?.GetService<AutoUpdateService>()?.GetCurrentVersion() ?? "0.78.6";
            Title = $"SQL Health Assessment v{version}";
            
            // Add keyboard shortcut for DevTools (F12)
            KeyDown += OnKeyDown;
            
            // Add WebView error handling
            BlazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            _logger?.LogInformation("MainWindow loaded successfully");
        }

        private void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
        {
            try
            {
                _logger?.LogInformation("BlazorWebView initialized");

                // Wire up PrintService with the live CoreWebView2 instance
                var printService = App.Services?.GetService<Data.Services.PrintService>();
                printService?.SetWebView(e.WebView.CoreWebView2);

                // Add JavaScript error handling
                e.WebView.NavigationCompleted += (s, args) =>
                {
                    if (args.IsSuccess)
                    {
                        _logger?.LogDebug("WebView navigation completed successfully");
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
            }
        }

        private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F12)
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
            // Show the closing dialog
            ClosingOverlay.Visibility = Visibility.Visible;
            
            // Cancel the close to allow dialog to show
            e.Cancel = true;
            
            // Wait 0.8 seconds then close
            await Task.Delay(800);
            
            // Actually close the window
            Application.Current.Shutdown();
        }
    }
}
