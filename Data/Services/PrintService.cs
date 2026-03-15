/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Wraps CoreWebView2.PrintToPdfAsync to export the current page as a multi-page PDF,
    /// respecting @media print CSS rules.  Saves to .\output\ automatically.
    /// </summary>
    public class PrintService
    {
        private CoreWebView2? _webView;
        private readonly AuditLogService? _auditLog;

        /// <summary>
        /// Constructor with optional AuditLogService for audit trail.
        /// </summary>
        public PrintService(AuditLogService? auditLog = null)
        {
            _auditLog = auditLog;
        }

        public void SetWebView(CoreWebView2 webView)
        {
            _webView = webView;
        }

        public bool IsAvailable => _webView != null;

        /// <summary>
        /// Exports the current WebView page to PDF in the .\output\ folder.
        /// Returns (true, path, null) on success, (false, null, error) if unavailable or failed.
        /// </summary>
        public async Task<(bool Success, string? Path, string? Error)> PrintToPdfAsync(string fileName = "Report.pdf")
        {
            if (_webView == null)
            {
                _auditLog?.LogExportOperation("PDF", fileName, false, "WebView not available");
                return (false, null, "WebView not available");
            }

            var outputDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var filePath = System.IO.Path.Combine(outputDir, fileName);

            // RunContinuationsAsynchronously prevents inline continuation deadlocks
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // All WebView2 API calls (CreatePrintSettings + PrintToPdfAsync) must run on the WPF UI thread.
            // Using an async lambda so we can properly await PrintToPdfAsync within the dispatcher.
            Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var settings = _webView.Environment.CreatePrintSettings();
                    settings.Orientation            = CoreWebView2PrintOrientation.Landscape;
                    settings.ShouldPrintBackgrounds = false;
                    settings.MarginTop    = 0.4;
                    settings.MarginBottom = 0.4;
                    settings.MarginLeft   = 0.4;
                    settings.MarginRight  = 0.4;

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
                System.Diagnostics.Debug.WriteLine($"PDF export error: {ex.Message}");
                return (false, null, ex.Message);
            }
        }
    }
}
