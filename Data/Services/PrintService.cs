/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.Web.WebView2.Core;

namespace SQLTriage.Data.Services
{
    // BM:PrintService.Class — exports current page to PDF via WebView2 or browser print
    /// <summary>
    /// Wraps CoreWebView2.PrintToPdfAsync to export the current page as a multi-page PDF,
    /// respecting @media print CSS rules.  Saves to .\output\ automatically.
    /// Falls back to browser window.print() in server mode (no WebView2).
    /// </summary>
    public class PrintService : IPrintService
    {
        private CoreWebView2? _webView;
        private readonly AuditLogService? _auditLog;
        private readonly ILogger<PrintService> _logger;

        /// <summary>
        /// Constructor with optional AuditLogService for audit trail.
        /// </summary>
        public PrintService(ILogger<PrintService> logger, AuditLogService? auditLog = null)
        {
            _logger = logger;
            _auditLog = auditLog;
        }

        public void SetWebView(CoreWebView2 webView)
        {
            _webView = webView;
        }

        public bool IsAvailable => _webView != null;

        /// <summary>
        /// Exports the current WebView page to PDF in the .\output\ folder.
        /// printBackgrounds: true preserves dark-theme colours (dashboards);
        ///                   false forces a white print (Quick Check / text reports).
        /// Returns (true, path, null) on success, (false, null, error) if unavailable or failed.
        /// </summary>
        public async Task<(bool Success, string? Path, string? Error)> PrintToPdfAsync(
            string fileName = "Report.pdf",
            bool printBackgrounds = false,
            CoreWebView2PrintOrientation orientation = CoreWebView2PrintOrientation.Landscape,
            string? headerTitle = null,
            string? footerUri = null,
            int timeoutSeconds = 30)
        {
            if (_webView == null)
            {
                _auditLog?.LogExportOperation("PDF", fileName, false, "WebView not available");
                return (false, null, "WebView not available — use browser print (Ctrl+P) or call PrintViaBrowserAsync.");
            }

            var outputDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var filePath = System.IO.Path.Combine(outputDir, fileName);

            // RunContinuationsAsynchronously prevents inline continuation deadlocks
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // All WebView2 API calls (CreatePrintSettings + PrintToPdfAsync) must run on the WPF UI thread.
            Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var settings = _webView.Environment.CreatePrintSettings();
                    settings.Orientation = orientation;
                    settings.ShouldPrintBackgrounds = printBackgrounds;
                    settings.ShouldPrintHeaderAndFooter = headerTitle != null || footerUri != null;
                    if (headerTitle != null) settings.HeaderTitle = headerTitle;
                    if (footerUri != null) settings.FooterUri = footerUri;
                    // 5 mm margins all round (~0.2 in); bottom slightly taller for DOM footer bar
                    settings.MarginTop = 0.2;
                    settings.MarginBottom = 0.25;
                    settings.MarginLeft = 0.2;
                    settings.MarginRight = 0.2;

                    await _webView.PrintToPdfAsync(filePath, settings);
                    tcs.TrySetResult(true);
                }
                catch (System.Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completed == timeoutTask)
                {
                    _logger.LogError("PDF export timed out after {Seconds}s for file {FileName}", timeoutSeconds, fileName);
                    _auditLog?.LogExportOperation("PDF", fileName, false, $"Timed out after {timeoutSeconds}s");
                    return (false, null, $"PDF export timed out after {timeoutSeconds} seconds.");
                }

                await tcs.Task; // unwrap any exception
                _auditLog?.LogExportOperation("PDF", fileName, true);
                return (true, filePath, null);
            }
            catch (System.Exception ex)
            {
                _auditLog?.LogExportOperation("PDF", fileName, false, ex.Message);
                _logger.LogError(ex, "PDF export failed for file {FileName}", fileName);
                return (false, null, ex.Message);
            }
        }

        /// <summary>
        /// Writes HTML to a temp file, navigates WebView2 to it, captures as PDF, then navigates back.
        /// No new packages required — reuses CoreWebView2.PrintToPdfAsync on a file:// URL.
        /// </summary>
        public async Task<(bool Success, string? Path, string? Error)> PrintHtmlToPdfAsync(
            string html,
            string outputPath,
            bool printBackgrounds = false,
            CoreWebView2PrintOrientation orientation = CoreWebView2PrintOrientation.Portrait,
            string? headerTitle = null,
            int timeoutSeconds = 60)
        {
            if (_webView == null)
            {
                _auditLog?.LogExportOperation("PDF", outputPath, false, "WebView not available");
                return (false, null, "WebView not available — PDF export requires the desktop (WebView2) mode.");
            }

            // Write HTML to a unique temp file so the WebView can load it via file://
            var tempFile = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sqltriage_report_{Guid.NewGuid():N}.html");

            // Hoisted to method scope so the finally block can ALWAYS restore the
            // host WebView. If we only navigate back on success, any print
            // failure/timeout strands the user on the raw file:// HTML with the
            // Blazor circuit gone (no error Toast can render). Fail safe, not silent.
            string? returnUrl = null;

            try
            {
                await System.IO.File.WriteAllTextAsync(tempFile, html, System.Text.Encoding.UTF8);

                // Capture the URL the WebView is currently showing so we can restore it afterward.
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    returnUrl = _webView.Source;
                });

                // Navigate to the temp file and wait for NavigationCompleted.
                var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    _webView.NavigationCompleted -= OnNavCompleted;
                    navTcs.TrySetResult(args.IsSuccess);
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _webView.NavigationCompleted += OnNavCompleted;
                    _webView.Navigate($"file:///{tempFile.Replace('\\', '/')}");
                });

                using var navCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds / 2));
                var navTimeout = Task.Delay(Timeout.Infinite, navCts.Token);
                var navResult = await Task.WhenAny(navTcs.Task, navTimeout);

                if (navResult == navTimeout || !await navTcs.Task)
                {
                    _logger.LogError("HTML navigation timed out or failed for {OutputPath}", outputPath);
                    _auditLog?.LogExportOperation("PDF", outputPath, false, "Navigation to temp HTML failed");
                    return (false, null, "Navigation to temp HTML timed out or failed.");
                }

                // Small settle delay so the browser renders fully before capture.
                await Task.Delay(300);

                // Print the now-loaded page to PDF.
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

                var printTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        var settings = _webView.Environment.CreatePrintSettings();
                        settings.Orientation = orientation;
                        settings.ShouldPrintBackgrounds = printBackgrounds;
                        settings.ShouldPrintHeaderAndFooter = headerTitle != null;
                        if (headerTitle != null) settings.HeaderTitle = headerTitle;
                        settings.MarginTop = 0.2;
                        settings.MarginBottom = 0.25;
                        settings.MarginLeft = 0.2;
                        settings.MarginRight = 0.2;
                        await _webView.PrintToPdfAsync(outputPath, settings);
                        printTcs.TrySetResult(true);
                    }
                    catch (System.Exception ex)
                    {
                        printTcs.TrySetException(ex);
                    }
                });

                using var printCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds / 2));
                var printTimeout = Task.Delay(Timeout.Infinite, printCts.Token);
                var printCompleted = await Task.WhenAny(printTcs.Task, printTimeout);

                if (printCompleted == printTimeout)
                {
                    _logger.LogError("PDF capture timed out for {OutputPath}", outputPath);
                    _auditLog?.LogExportOperation("PDF", outputPath, false, "PDF capture timed out");
                    return (false, null, "PDF capture timed out.");
                }

                await printTcs.Task; // unwrap any exception

                _auditLog?.LogExportOperation("PDF", outputPath, true);
                return (true, outputPath, null);
            }
            catch (System.Exception ex)
            {
                _auditLog?.LogExportOperation("PDF", outputPath, false, ex.Message);
                _logger.LogError(ex, "PrintHtmlToPdfAsync failed for {OutputPath}", outputPath);
                return (false, null, ex.Message);
            }
            finally
            {
                // ALWAYS restore the host WebView — success or failure — so a failed
                // export never strands the user on the raw temp HTML (the Blazor app
                // lives in this same WebView). Best-effort.
                // Restore to the captured URL; if it was blank (BlazorWebView
                // .Source is often empty) fall back to the app root so we NEVER
                // strand on the temp HTML — that was the reported failure mode.
                var restoreTo = !string.IsNullOrEmpty(returnUrl) ? returnUrl : "http://0.0.0.0/";
                try
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _webView.Navigate(restoreTo);
                    });
                }
                catch { /* ignored — nothing better we can do here */ }

                // Best-effort cleanup — leave the file if deletion fails (OS lock during capture).
                try { System.IO.File.Delete(tempFile); } catch { /* ignored */ }
            }
        }

        /// <summary>
        /// Falls back to the browser's native print dialog via JS interop.
        /// Used in server mode where WebView2 is not available.
        /// </summary>
        public async Task<(bool Success, string? Path, string? Error)> PrintViaBrowserAsync(IJSRuntime jsRuntime)
        {
            try
            {
                await jsRuntime.InvokeVoidAsync("window.print");
                _auditLog?.LogExportOperation("PDF", "browser-print", true);
                return (true, null, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Browser print failed");
                return (false, null, ex.Message);
            }
        }
    }
}
