/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.Web.WebView2.Core;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Wraps CoreWebView2.PrintToPdfAsync to export the current page as a multi-page PDF,
    /// respecting @media print CSS rules.  Saves to .\output\ automatically.
    /// Falls back to browser window.print() in server mode (no WebView2).
    /// </summary>
    public class PrintService
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
            string? footerUri = null)
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
                    settings.Orientation            = orientation;
                    settings.ShouldPrintBackgrounds = printBackgrounds;
                    settings.ShouldPrintHeaderAndFooter = headerTitle != null || footerUri != null;
                    if (headerTitle != null) settings.HeaderTitle = headerTitle;
                    if (footerUri  != null) settings.FooterUri   = footerUri;
                    // 5 mm margins all round (~0.2 in); bottom slightly taller for DOM footer bar
                    settings.MarginTop    = 0.2;
                    settings.MarginBottom = 0.25;
                    settings.MarginLeft   = 0.2;
                    settings.MarginRight  = 0.2;

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
                await tcs.Task;
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
